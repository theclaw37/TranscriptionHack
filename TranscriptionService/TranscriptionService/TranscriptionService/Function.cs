using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.S3Events;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializerAttribute(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace TranscriptionService
{
    public class Functions
    {
        private const string VOCI_TOKEN = "VociToken";

        IDynamoDBContext DDBContext { get; set; }
        IAmazonS3 S3Client { get; set; }
        AmazonDynamoDBClient DDBClient { get; set; }

        /// <summary>
        /// Default constructor that Lambda will invoke.
        /// </summary>
        public Functions()
        {
            AWSConfigsDynamoDB.Context.TypeMappings[typeof(Transcript)] = new Amazon.Util.TypeMapping(typeof(Transcript), "Transcripts");            

            var config = new DynamoDBContextConfig { Conversion = DynamoDBEntryConversion.V2 };
            DDBClient = new AmazonDynamoDBClient();
            DDBContext = new DynamoDBContext(DDBClient, config);
            S3Client = new AmazonS3Client();
        }

        /// <summary>
        /// This method is called for every Lambda invocation. This method takes in an S3 event object and can be used 
        /// to respond to S3 notifications.
        /// </summary>
        /// <param name="evnt"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task InitTranscript(S3Event evnt, ILambdaContext context)
        {
            var s3Event = evnt.Records?[0].S3;
            if (s3Event == null)
            {
                return;
            }

            try
            {
                var preSignedUrlRequest = new GetPreSignedUrlRequest
                {
                    BucketName = s3Event.Bucket.Name,
                    Key = s3Event.Object.Key,
                    Expires = DateTime.Now.AddHours(6)
                };

                var transcript = new Transcript
                {
                    CreatedOn = DateTime.Now,
                    ModifiedOn = DateTime.Now,
                    Id = s3Event.Object.Key,
                    FileUrl = S3Client.GetPreSignedURL(preSignedUrlRequest)                  
                };               

                context.Logger.LogLine($"Saving transcript with id {transcript.Id}");
                await DDBContext.SaveAsync(transcript);              
            }
            catch (Exception e)
            {
                context.Logger.LogLine($"Error getting object {s3Event.Object.Key} from bucket {s3Event.Bucket.Name}. Make sure they exist and your bucket is in the same region as this function.");
                context.Logger.LogLine(e.Message);
                context.Logger.LogLine(e.StackTrace);
                throw;
            }
        }

        /// <summary>
        /// A test Lambda function to respond to HTTP Get methods from API Gateway
        /// </summary>
        /// <param name="request"></param>
        /// <returns>A "hello world" string`</returns>
        public APIGatewayProxyResponse Get(APIGatewayProxyRequest request, ILambdaContext context)
        {
            context.Logger.LogLine("Get Request\n");

            var response = new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = "Hello AWS Serverless",
                Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } }
            };

            return response;
        }

        public async Task<APIGatewayProxyResponse> ProcessTranscripts(APIGatewayProxyRequest request, ILambdaContext context)
        {
            context.Logger.LogLine("Getting transcripts");
            var search = DDBContext.ScanAsync<Transcript>(new[] {new ScanCondition("VociRequestId", ScanOperator.IsNull) });
            var transcripts = await search.GetNextSetAsync();
            context.Logger.LogLine($"Found {transcripts.Count} transcripts");

            foreach (var transcript in transcripts)
            {
                await SendTranscriptToVoci(transcript);
                context.Logger.LogLine($"Transcirpot processed: {transcript.Id} - requestId: {transcript.VociRequestId}");
            }

            context.Logger.LogLine("Save transcript started");
            //await DDBContext.SaveAsync(transcripts, new DynamoDBOperationConfig { Conversion = DynamoDBEntryConversion.V2, IndexName = "Id" });
            UpdateTranscripts(transcripts);
            context.Logger.LogLine("Save transcript ended");
            var response = new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = JsonConvert.SerializeObject(transcripts),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };

            return response;
        }

        public async Task<APIGatewayProxyResponse> GetTranscripts(APIGatewayProxyRequest request, ILambdaContext context)
        {
            var client = new HttpClient();

            context.Logger.LogLine("Getting transcripts");
            var search = DDBContext.ScanAsync<Transcript>(new[]
            {
                new ScanCondition("VociTranscript", ScanOperator.IsNull),
                new ScanCondition("VociRequestId", ScanOperator.IsNotNull)
            });

            var transcripts = await search.GetNextSetAsync();
            context.Logger.LogLine($"Found {transcripts.Count} transcripts");

            var token = Environment.GetEnvironmentVariable(VOCI_TOKEN);

            foreach (var transcript in transcripts)
            {                
                var urlTranscribeResult =
                    $"https://vcloud.vocitec.com/transcribe/result?token={token}&requestid={transcript.VociRequestId}";
                var transcribeResultResponse = await client.GetAsync(urlTranscribeResult);
                if (transcribeResultResponse.IsSuccessStatusCode)
                {
                    transcript.VociTranscript = await transcribeResultResponse.Content.ReadAsStringAsync();
                }                
            }

            context.Logger.LogLine($"Received transcribtions");


            //await DDBContext.SaveAsync(transcripts, new DynamoDBOperationConfig {Conversion = DynamoDBEntryConversion.V2, IndexName = "Id"});
            UpdateTranscripts(transcripts);

            context.Logger.LogLine($"Updated transcribtions");

            var response = new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = JsonConvert.SerializeObject(transcripts),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };

            return response;
        }

        private async void  UpdateTranscripts(List<Transcript> transcripts)
        {
            foreach (var transcript in transcripts)
            {
                Dictionary<string, AttributeValue> key = new Dictionary<string, AttributeValue>
                {
                    { "Id", new AttributeValue { S = transcript.Id } }                    
                };

                // Define attribute updates
                Dictionary<string, AttributeValueUpdate> updates = new Dictionary<string, AttributeValueUpdate>();
                // Update item's Setting attribute
                if (string.IsNullOrEmpty(transcript.VociTranscript))
                {
                    updates["VociRequestId"] = new AttributeValueUpdate
                    {
                        Action = AttributeAction.PUT,
                        Value = new AttributeValue {S = transcript.VociRequestId}
                    };
                }
                else
                {
                    updates["VociTranscript"] = new AttributeValueUpdate
                    {
                        Action = AttributeAction.PUT,
                        Value = new AttributeValue { S = transcript.VociTranscript }
                    };
                }

                // Create UpdateItem request
                UpdateItemRequest request = new UpdateItemRequest
                {
                    TableName = "Transcripts",
                    Key = key,
                    AttributeUpdates = updates
                };

                // Issue request
                await DDBClient.UpdateItemAsync(request);
            }
        }

        private async Task SendTranscriptToVoci(Transcript transcript)
        {
            var client = new HttpClient();

            using (var zipStream = new MemoryStream())
            {
                using (var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                {
                    var zipEntry = zipArchive.CreateEntry(transcript.Id);
                    using (var zipEntryStream = zipEntry.Open())
                    using (var stream = await client.GetStreamAsync(transcript.FileUrl))
                        stream.CopyToAsync(zipEntryStream).Wait();
                }
                
                var url = @"https://vcloud.vocitec.com/transcribe";

                var token = Environment.GetEnvironmentVariable(VOCI_TOKEN);
                var tokenContent = new StringContent(token);

                zipStream.Seek(0, SeekOrigin.Begin);
                var fileContent = new StreamContent(zipStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

                var content = new MultipartFormDataContent
                {
                    {tokenContent, "token"},
                    {fileContent, "file", "file.zip"}
                };

                var vociResponse = await client.PostAsync(url, content);
                var json = JObject.Parse(await vociResponse.Content.ReadAsStringAsync());
                transcript.VociRequestId = json.Value<string>("requestid");
            }
        }              
    }
}
