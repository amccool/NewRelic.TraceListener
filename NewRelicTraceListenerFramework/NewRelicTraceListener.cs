using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;

namespace NewRelicTraceListenerFramework
{
	/// <summary>
	/// A TraceListener class used to submit trace data to New Relic via .NET Agent API
	/// RecordCustomEvent
	/// <see cref="https://docs.newrelic.com/docs/agents/net-agent/net-agent-api/record-custom-event"/>
	/// </summary>
	public class NewRelicTraceListener : TraceListenerBase
	{
		private readonly BlockingCollection<Dictionary<string,object>> _queueToBePosted = new BlockingCollection<Dictionary<string, object>>();

		private string _userDomainName;
		private string _userName;

		private static readonly string[] _supportedAttributes = new string[]
		{ };

		/// <summary>
		/// Allowed attributes for this trace listener.
		/// </summary>
		protected override string[] GetSupportedAttributes()
		{
			return _supportedAttributes;
		}



		/// <summary>
		/// Gets a value indicating the trace listener is thread safe.
		/// </summary>
		/// <value>true</value>
		public override bool IsThreadSafe => true;

		/// <summary>
		/// We cant grab any of the attributes until the class and more importantly its base class has finsihed initializing
		/// so keep the constructor at a minimum
		/// </summary>
		public NewRelicTraceListener() : base()
		{
			_userDomainName = Environment.UserDomainName;
			_userName = Environment.UserName;
			_machineName = Environment.MachineName;
			Initialize();
		}

		/// <summary>
		/// We cant grab any of the attributes until the class and more importantly its base class has finsihed initializing
		/// so keep the constructor at a minimum
		/// </summary>
		public NewRelicTraceListener(string name) : base(name)
		{
			_userDomainName = Environment.UserDomainName;
			_userName = Environment.UserName;
			_machineName = Environment.MachineName;
			Initialize();
		}

		private void Initialize()
		{
			//SetupObserver();
			SetupObserverBatchy();
		}

		private Action<Dictionary<string,object>> _scribeProcessor;
		private string _machineName;

		//private void SetupObserver()
		//{
		//	_scribeProcessor = a => WriteDirectlyToES(a);

		//	//this._queueToBePosted.GetConsumingEnumerable()
		//	//.ToObservable(Scheduler.Default)
		//	//.Subscribe(x => WriteDirectlyToES(x));
		//}


		private void SetupObserverBatchy()
		{
			_scribeProcessor = a => WriteToQueueForprocessing(a);

			this._queueToBePosted.GetConsumingEnumerable()
				.ToObservable(Scheduler.Default)
				.Buffer(TimeSpan.FromSeconds(1), 10)
				.Subscribe(async x => await this.WriteDirectlyToNewRelicAsBatch(x));
		}



		/// <summary>
		/// Write trace event with data.
		/// </summary>
		protected override void WriteTrace(
			TraceEventCache eventCache,
			string source,
			TraceEventType eventType,
			int id,
			string message,
			Guid? relatedActivityId,
			object data)
		{

			//if (eventCache != null && eventCache.Callstack.Contains(nameof(Elasticsearch.Net.ElasticLowLevelClient)))
			//{
			//    return;
			//}

			string updatedMessage = message;
			JObject payload = null;
			if (data != null)
			{
				if (data is Exception)
				{
					updatedMessage = ((Exception)data).Message;
					payload = JObject.FromObject(data);
				}
				else if (data is XPathNavigator)
				{
					var xdata = data as XPathNavigator;
					//xdata.MoveToRoot();

					XDocument xmlDoc;
					try
					{
						xmlDoc = XDocument.Parse(xdata.OuterXml);

					}
					catch (Exception)
					{
						xmlDoc = XDocument.Parse(xdata.ToString());
						//eat
						//throw;
					}

					// Convert the XML document in to a dynamic C# object.
					dynamic xmlContent = new ExpandoObject();
					ExpandoObjectHelper.Parse(xmlContent, xmlDoc.Root);

					string json = JsonConvert.SerializeObject(xmlContent);
					payload = JObject.Parse(json);
				}
				else if (data is DateTime)
				{
					payload = new JObject();
					payload.Add("System.DateTime", (DateTime)data);
				}
				else if (data is string)
				{
					payload = new JObject();
					payload.Add("string", (string)data);
				}
				else if (data.GetType().IsValueType)
				{
					payload = new JObject { { "data", data.ToString() } };
				}
				else
				{
					try
					{
						payload = JObject.FromObject(data);
					}
					catch (JsonSerializationException jEx)
					{
						payload = new JObject();
						payload.Add("FAILURE", jEx.Message);
						payload.Add("data", data.GetType().ToString());
					}
				}
			}

			//Debug.Assert(!string.IsNullOrEmpty(updatedMessage));
			//Debug.Assert(payload != null);

			InternalWrite(eventCache, source, eventType, id, updatedMessage, relatedActivityId, payload);
		}

		private void InternalWrite(
			TraceEventCache eventCache,
			string source,
			TraceEventType eventType,
			int? traceId,
			string message,
			Guid?
				relatedActivityId,
			JObject dataObject)
		{

			DateTime logTime;
			string logicalOperationStack = null;
			if (eventCache != null)
			{
				logTime = eventCache.DateTime.ToUniversalTime();
				if (eventCache.LogicalOperationStack != null && eventCache.LogicalOperationStack.Count > 0)
				{
					StringBuilder stackBuilder = new StringBuilder();
					foreach (object o in eventCache.LogicalOperationStack)
					{
						if (stackBuilder.Length > 0) stackBuilder.Append(", ");
						stackBuilder.Append(o);
					}
					logicalOperationStack = stackBuilder.ToString();
				}
			}
			else
			{
				logTime = DateTimeOffset.UtcNow.UtcDateTime;
			}

			string threadId = eventCache != null ? eventCache.ThreadId : string.Empty;
			string thread = Thread.CurrentThread.Name ?? threadId;




			IPrincipal principal = Thread.CurrentPrincipal;
			IIdentity identity = principal?.Identity;
			string identityname = identity == null ? string.Empty : identity.Name;

			string username = $"{_userDomainName}\\{_userName}";

			try
			{
				var trace = new Dictionary<string, object>
				{
					{"Source", source },
					{"TraceId", traceId ?? 0},
					{"EventType", eventType.ToString()},
					{"UtcDateTime", logTime},
					{"timestamp", eventCache?.Timestamp ?? 0},
					{"MachineName", _machineName},
					{"AppDomainFriendlyName", AppDomain.CurrentDomain.FriendlyName},
					{"ProcessId", eventCache?.ProcessId ?? 0},
					{"ThreadName", thread},
					{"ThreadId", threadId},
					{"Message", message},
					{"ActivityId", Trace.CorrelationManager.ActivityId != Guid.Empty ? Trace.CorrelationManager.ActivityId.ToString() : string.Empty},
					{"RelatedActivityId", relatedActivityId.HasValue ? relatedActivityId.Value.ToString() : string.Empty},
					{"LogicalOperationStack", logicalOperationStack},
					{"Data", dataObject},
					{"Username", username},
					{"Identityname", identityname},
				};

				_scribeProcessor(trace);
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
			}
		}


		private async Task WriteDirectlyToNewRelicAsBatch(IEnumerable<IDictionary<string, object>> jos)
		{
			if (jos.Count() < 1)
				return;


			foreach (var json in jos)
			{
				try
				{
					NewRelic.Api.Agent.NewRelic.RecordCustomEvent(@"Trace", json);
				}
				catch (Exception ex)
				{
					Debug.WriteLine(ex);
				}

			}


		}

		private void WriteToQueueForprocessing(Dictionary<string,object> jo)
		{
			this._queueToBePosted.Add(jo);
		}


		/// <summary>
		/// removing the spin flush
		/// </summary>
		public override void Flush()
		{
			//check to make sure the "queue" has been emptied
			//while (this._queueToBePosted.Count() > 0)            { }
			base.Flush();
		}

		protected override void Dispose(bool disposing)
		{
			this._queueToBePosted.Dispose();
			base.Flush();
			base.Dispose(disposing);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}
	}

}
