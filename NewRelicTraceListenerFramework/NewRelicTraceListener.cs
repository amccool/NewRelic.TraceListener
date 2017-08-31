using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using Daishi.NewRelic.Insights;
using Jil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NewRelicTraceListenerFramework
{
    /// <summary>
    /// A TraceListener class used to submit trace data to New Relic via .NET Agent API
    /// RecordCustomEvent
    /// <see cref="https://docs.newrelic.com/docs/agents/net-agent/net-agent-api/record-custom-event"/>
    /// </summary>
    public class NewRelicTraceListener : TraceListenerBase
	{
		private string _userDomainName;
		private string _userName;
		private string _machineName;

		private static readonly string[] _supportedAttributes = new string[]
		{
		    //"AccountID", "AccountId", "accountid","accountId",
		    //"APIKey", "apiKey", "apikey", "APIkey", "apiKEY",
		    //"insightsURI", "insightsuri", "insightsUri",
        };

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
			//Initialize();
		}

		/// <summary>
		/// We cant grab any of the attributes until the class and more importantly its base class has finsihed initializing
		/// so keep the constructor at a minimum
		/// </summary>
		public NewRelicTraceListener(string configData)
		{
			_userDomainName = Environment.UserDomainName;
			_userName = Environment.UserName;
			_machineName = Environment.MachineName;


            //parse the config data

		    Dictionary<string, string> keyValuePairs = configData.Split(';')
		        .Select(value => value.Split('='))
		        .ToDictionary(pair => pair[0].ToLower(), pair => pair[1]);

		    string accountid = keyValuePairs["accountid"];
		    string apikey = keyValuePairs["apikey"];

		    var insightsuri = @"https://insights-collector.newrelic.com/v1/accounts";

            if (keyValuePairs.ContainsKey("insightsuri"))
		    {
		        insightsuri = keyValuePairs["insightsuri"];
		    }


		    Initialize(accountid, apikey, insightsuri);
		}

        private void Initialize(string accountid, string apikey, string insightsuri)
		{
		    NewRelicInsightsClient.Instance.NewRelicInsightsMetadata.AccountID = accountid;
		    NewRelicInsightsClient.Instance.NewRelicInsightsMetadata.APIKey = apikey;
		    NewRelicInsightsClient.Instance.NewRelicInsightsMetadata.URI = new Uri(insightsuri);
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
					{"source", source },
					{"traceId", traceId ?? 0},
					{"eventType", eventType.ToString()},
					{"utcDateTime", logTime},
					{"ticks", eventCache?.Timestamp ?? 0},
					{"machineName", _machineName},
					{"appDomainFriendlyName", AppDomain.CurrentDomain.FriendlyName},
					{"processId", eventCache?.ProcessId ?? 0},
					{"threadName", thread},
					{"threadId", threadId},
					{"message", message},
					{"activityId", Trace.CorrelationManager.ActivityId != Guid.Empty ? Trace.CorrelationManager.ActivityId.ToString() : string.Empty},
					{"relatedActivityId", relatedActivityId.HasValue ? relatedActivityId.Value.ToString() : string.Empty},
					{"logicalOperationStack", logicalOperationStack},
					{"data", dataObject},
					{"username", username},
					{"identityname", identityname},
				};

				WriteToNewRelic(trace);
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
			}
		}

	    private void WriteToNewRelic(Dictionary<string, object> trace)
	    {
	        try
	        {
	            UploadEvents(
	                Enumerable.Repeat(trace, 1),
	                new HttpClientFactory());
	        }
	        catch (Exception ex)
	        {
	            Debug.WriteLine(ex);
	        }
	    }


	    public void UploadEvents(
            //IEnumerable<NewRelicInsightsEvent> newRelicInsightsEvents,
            IEnumerable<Dictionary<string,object>> events,
            HttpClientFactory httpClientFactory)
	    {
	        if (events == null || !events.Any())
	        {
	            return;
	        }

	        NewRelicInsightsMetadataException newRelicInsightsMetadataException;

	        var newRelicInsightsMetadataIsValid =
	            NewRelicInsightsMetadataValidator.TryValidate(NewRelicInsightsClient.Instance.NewRelicInsightsMetadata,
	                out newRelicInsightsMetadataException);

	        if (!newRelicInsightsMetadataIsValid)
	        {
	            throw newRelicInsightsMetadataException;
	        }

	        HttpClientHandler httpClientHandler;

	        using (var httpClient = httpClientFactory.Create(NewRelicInsightsClient.Instance.NewRelicInsightsMetadata,
	            out httpClientHandler))
	        {
	            httpClient.DefaultRequestHeaders.Accept.Clear();

	            httpClient.DefaultRequestHeaders.Accept.Add(
	                new MediaTypeWithQualityHeaderValue("application/json"));

	            var apiKeyHeaderIsAdded = NewRelicInsightsCustomHttpHeaderInjecter.TryInjectAPIKey(
	                "X-Insert-Key",
	                NewRelicInsightsClient.Instance.NewRelicInsightsMetadata.APIKey,
	                httpClient.DefaultRequestHeaders,
	                out newRelicInsightsMetadataException);

	            if (!apiKeyHeaderIsAdded) throw newRelicInsightsMetadataException;

	            StringWriter stringWriter;
	            HttpResponseMessage httpResponseMessage;

	            using (stringWriter = new StringWriter())
	            {
	                try
	                {
	                    JSON.SerializeDynamic(events, stringWriter, Options.IncludeInherited);

	                    httpResponseMessage = httpClient.PostAsync(
	                        string.Concat(
	                            NewRelicInsightsClient.Instance.NewRelicInsightsMetadata.URI, "/",
	                            NewRelicInsightsClient.Instance.NewRelicInsightsMetadata.AccountID,
	                            "/events"),
	                        new StringContent(
	                            stringWriter.ToString(),
	                            Encoding.UTF8,
	                            "application/json")
	                    ).Result;
	                }
	                catch (Exception exception)
	                {
	                    throw new NewRelicInsightsEventUploadException(
	                        "An error occurred while uploading events to New Relic Insights",
	                        exception);
	                }
	            }

	            NewRelicInsightsResponse newRelicInsightsResponse;

	            try
	            {
	                var httpResponseContent =
	                    httpResponseMessage.Content.ReadAsStringAsync().Result;

	                newRelicInsightsResponse =
	                    NewRelicInsightsResponseParser.Parse(
	                        httpResponseMessage.IsSuccessStatusCode,
	                        httpResponseContent);
	            }
	            catch (Exception exception)
	            {
	                throw new NewRelicInsightsEventUploadException(
	                    "An error occurred while parsing the New Relic Insights HTTP response.",
	                    exception);
	            }

	            if (!newRelicInsightsResponse.Success)
	            {
	                httpResponseMessage.Content?.Dispose();
	                throw new UnableToParseNewRelicInsightsResponseException(
	                    newRelicInsightsResponse.Message);
	            }
	        }
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
			//this._queueToBePosted.Dispose();
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
