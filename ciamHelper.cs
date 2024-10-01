using System;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Azure.Identity;
using Microsoft.Graph.Beta;
using Microsoft.Graph.Beta.Models;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Net.Http;

namespace readUser
{
    public static class ciamHelper
    {
        [FunctionName("ciamHelper")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            //log.LogInformation("C# HTTP trigger function processed a request.");
            Console.WriteLine("\n" + "C# HTTP trigger function processed a request.");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            string objectId = data.objectId;
            string email = data.email;
            string password = data.password;
            string method = data.method;
            string phoneNumber = data.phoneNumber;
            string displayName = data.displayName;
            string givenName = data.givenName;
            string surName = data.surName;

            Console.WriteLine("\n" + "Object Id " + objectId + " Email " + email + " Password " + password + " Method " + method 
                + " Phone number " + phoneNumber + " Display name " + displayName + " Given name " + givenName + " Surname " + surName);

            // TODO: Add Entra External IDP tenant ID
            var tenantId = "your secret ";

            if (method == "auth")
            {
                Console.WriteLine("\n" + "Authenticating user");

                using (var httpClient = new HttpClient())
                {
                    // Build the request URL
                    //var requestUrl = "https://eeidtenant.ciamlogin.com/eeidobjectId/oauth2/token";

                    // TODO: Add Entra External IDP tenant name and ID
                    var requestUrl = "https://eeidtenant.ciamlogin.com/eeidobjectId/oauth2/v2.0/token";
                    //string auth_resource = "https://graph.microsoft.com"; // Replace with your specific resource URL
                    string scope = "https://graph.microsoft.com/.default";
                    // TODO: Add RopcFromB2C client ID
                    string auth_clientId = "your clientID ";

                    // Prepare the request body
                    //var auth_requestBody = $"resource={auth_resource}&client_id={auth_clientId}&grant_type=password&username={email}&password={password}&nca=1";
                    var auth_requestBody = $"scope={scope}&client_id={auth_clientId}&grant_type=password&username={email}&password={password}&nca=1";

                    // Convert the request body to a byte array
                    var content = new StringContent(auth_requestBody, Encoding.UTF8, "application/x-www-form-urlencoded");

                    // Send the POST request to Azure AD
                    using (var response = await httpClient.PostAsync(requestUrl, content))
                    {
                        // Check if the request was successful
                        if (response.IsSuccessStatusCode)
                        {
                            // Read the response content
                            var responseContent = await response.Content.ReadAsStringAsync();
                            var jsonObj = JsonConvert.DeserializeObject(responseContent);
                            return new OkObjectResult(jsonObj);
                        }
                        else
                        {
                            // Handle error cases here
                            // For example, log the error or throw an exception
                            return new ConflictObjectResult(new B2CResponseModel($"Invalid username or password.", HttpStatusCode.Conflict));
                        }
                    }
                }
            }

            else
            {
                var scopes = new[] { "https://graph.microsoft.com/.default" };
                // Values from app registration  

                // TODO: Add GraphCallsFromB2CTenant client ID and secret
                var clientId = "your clientID";
                var clientSecret = "your client secret";

                // using Azure.Identity;  
                var options = new TokenCredentialOptions
                {
                    AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
                };

                // https://learn.microsoft.com/dotnet/api/azure.identity.clientsecretcredential  
                var clientSecretCredential = new ClientSecretCredential(
                    tenantId, clientId, clientSecret, options);

                // get accessToken          
                var accessToken = await clientSecretCredential.GetTokenAsync(new Azure.Core.TokenRequestContext(scopes) { });

                Console.WriteLine("\n" + accessToken.Token);

                var graphClient = new GraphServiceClient(clientSecretCredential, scopes);

                if (objectId != null && method == "read")
                {
                    Console.WriteLine("\n" + "ObjectId - Reading user");
                    
                    var user = await graphClient.Users[objectId].GetAsync();

                    //log.LogInformation(user.ToString());
                    var logGivenName = user.GivenName;
                    var logSurName = user.Surname;
                    var logDisplayName = user.DisplayName;
                    var logEmail = user.Identities[0].IssuerAssignedId;
                    var logPhoneNumber = user.Identities[1].IssuerAssignedId;
                    
                    Console.WriteLine("\n" + "Given name " + logGivenName + " Surname " + logSurName + " Display name " + logDisplayName);
                    Console.WriteLine("Email " + logEmail + " Phone number " + logPhoneNumber);

                    return new OkObjectResult(user);
                }

                if (email != null && method == "read")
                {
                    Console.WriteLine("\n" + "Email - Reading user");

                    var user = await graphClient.Users.GetAsync((requestConfiguration) =>
                    {
                        requestConfiguration.QueryParameters.Filter = string.Format("identities/any(x:x/issuerAssignedId eq '{0}' and x/issuer eq 'ciamprod.onmicrosoft.com')  ", email);
                    });

                    Console.WriteLine(user.ToString());

                    return new OkObjectResult(user);
                }

                if (method == "createUser")
                {
                    Console.WriteLine("\n" + "Creating user");
                    Console.WriteLine("Display name " + displayName + " email " + email );

                    var userRequestBody = new User
                    {
                        DisplayName = displayName,
                        GivenName = givenName,
                        Surname = surName,
                        Identities = new List<ObjectIdentity>
                    {
                        new ObjectIdentity
                        {
                            SignInType = "emailAddress",
                            // TODO: Add Entra External IDP tenant name
                            Issuer = "azureidextid.onmicrosoft.com",
                            IssuerAssignedId = email,
                        }
                    },
                        PasswordProfile = new PasswordProfile
                        {
                            Password = password,
                            ForceChangePasswordNextSignIn = false,
                        },
                        PasswordPolicies = "DisablePasswordExpiration",
                    };
                    
                    try
                    {
                        var result = await graphClient.Users.PostAsync(userRequestBody);
                        string stringObjectId = result.Id;

                            try 
                            {
                                await DoWithRetryAsync(TimeSpan.FromSeconds(1), tryCount: 10, stringObjectId, email, graphClient);
                           
                            }
                            catch (Exception enrolEx)
                            {
                                return new ConflictObjectResult(enrolEx);
                            }
                    
                        return new OkObjectResult(result);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("\n" + "Exception " + ex);
                        Console.WriteLine("\n" + "Error creating user - account already exists ");
                        
                        return new ConflictObjectResult(new B2CResponseModel($"This account already exists.", HttpStatusCode.Conflict));
                    }
                }

                if (method == "getPhone")
                {
                    Console.WriteLine("\n" + "Getting phone number");
                    
                    try
                    {
                        var result = await graphClient.Users[objectId].Authentication.PhoneMethods.GetAsync();
                        return new OkObjectResult(result.Value[0]);
                    }

                    catch (Exception exception)
                    {
                        Console.WriteLine("\n" + "Exception " + exception);
                        Console.WriteLine("\n" + "Adding phone number");

                        var jsonObject = new JObject();
                        jsonObject.Add("phoneNumber", "null");
                        return new OkObjectResult(jsonObject);
                    }

                }

                if (method == "setPhone")
                {
                    Console.WriteLine("\n" + "Setting phone number " + phoneNumber);

                    var mfaRequestBody = new PhoneAuthenticationMethod
                    {
                        PhoneNumber = phoneNumber,
                        PhoneType = AuthenticationPhoneType.Mobile,
                    };
                   
                    var enrolResult = await graphClient.Users[objectId].Authentication.PhoneMethods.PostAsync(mfaRequestBody);
                    return new OkObjectResult(enrolResult);
                }
            }

            return new OkObjectResult(null);
        }
        public static async Task EnrolEmail(GraphServiceClient graphClient, string email, string objectId){
            
            Console.WriteLine("\n" + "Enrolling email address:" + " email " + email + " objectId " + objectId);

            var emailAuthMethodRequestBody = new EmailAuthenticationMethod
            {
                EmailAddress = email
            };

            var result = await graphClient.Users[objectId].Authentication.EmailMethods.PostAsync(emailAuthMethodRequestBody);

            Console.WriteLine("\n" + "result " + result);
            //return new OkObjectResult(enrolResult);
        }
    
        //public static async Task DoWithRetryAsync(TimeSpan sleepPeriod, int tryCount = 3, string objectId="test", string email="test", GraphServiceClient graphClient=null)
         public static async Task DoWithRetryAsync(TimeSpan sleepPeriod, int tryCount, string objectId, string email, 
             GraphServiceClient graphClient)
        {
            Console.WriteLine("\n" + "DoWithRetryAsync");
            Console.WriteLine("\n" + "objectId " + objectId + " email " + email);
           
            if (tryCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(tryCount));

            while (true) {
                try {
                    await EnrolEmail(graphClient, email, objectId);
                    return;
                } catch {
                    if (--tryCount == 0)
                        throw;
                    await Task.Delay(sleepPeriod);
                }
            }
        }
    }

    public class B2CResponseModel
    {
        public string version { get; set; }
        public int status { get; set; }
        public string userMessage { get; set; }

        public B2CResponseModel(string message, HttpStatusCode status)
        {
            Console.WriteLine("\n" + "B2C response " + message);
            
            this.userMessage = message;
            this.status = (int)status;
            this.version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
        }
    }
}
