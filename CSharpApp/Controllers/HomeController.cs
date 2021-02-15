﻿using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using CSharpApp.Models;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;
using Microsoft.AspNetCore.WebUtilities;
using System;
using System.Text;

namespace CSharpApp.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;
        private static JDeere _settings = new JDeere();
        private static Status _status = new Status();
        public HomeController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public IActionResult Index()
        {
            _settings.ClientId = _configuration["JDeere:ClientId"];
            _settings.ClientSecret = _configuration["JDeere:ClientSecret"];
            _settings.WellKnown = _configuration["JDeere:WellKnown"];
            _settings.ServerUrl = _configuration["JDeere:ServerUrl"];
            _settings.CallbackUrl = _settings.ServerUrl + _configuration["JDeere:Callback"];
            _settings.UserCallbackUrl = _settings.ServerUrl + _configuration["JDeere:UserCallback"];
            _settings.Scopes = _configuration["JDeere:Scopes"];
            _settings.State = _configuration["JDeere:State"];
            _settings.APIURL = _configuration["JDeere:ApiUrl"];

            ViewBag.Settings = _settings;

            return View();
        }

        public IActionResult Callback()
        {
            ViewBag.Status = _status;

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Index(string ClientId, string ClientSecret, string WellKnown, string CallbackUrl, string Scopes, string State)
        {
            _settings.ClientId = ClientId;
            _settings.ClientSecret = ClientSecret;
            _settings.WellKnown = WellKnown;
            _settings.CallbackUrl = CallbackUrl;
            _settings.Scopes = Scopes;
            _settings.State = State;

            ViewBag.State = State;
            Dictionary<string, object> oAuthMetadata = await GetOAuthMetadata(WellKnown);
            string authEndpoint = oAuthMetadata["authorization_endpoint"].ToString();

            var queryParameters = new Dictionary<string, string>();
            queryParameters.Add("response_type", "code");
            queryParameters.Add("scope", Scopes);
            queryParameters.Add("client_id", ClientId);
            queryParameters.Add("state", State);
            queryParameters.Add("redirect_uri", CallbackUrl);

            string redirectUrl = QueryHelpers.AddQueryString(authEndpoint, queryParameters);

            return Redirect(redirectUrl);
        }


        [Route("/usercallback")]
        public IActionResult UserCallback(string state, string status, string errorcode, string message)
        {
            _status.state = state;
            _status.status = status;
            _status.errorcode = errorcode;
            _status.message = message;
            ViewBag.Status = _status;

            return View("Callback");
        }

        [Route("/callback")]
        public async Task<IActionResult> Callback(string code, string state)
        {
            Dictionary<string, object> oAuthMetadata = await GetOAuthMetadata(_settings.WellKnown);
            string tokenEndpoint = oAuthMetadata["token_endpoint"].ToString();

            var queryParameters = new Dictionary<string, string>();
            queryParameters.Add("grant_type", "authorization_code");
            queryParameters.Add("code", code);
            queryParameters.Add("redirect_uri", _settings.CallbackUrl);

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("authorization", $"Basic {GetBase64EncodedClientCredentials()}");

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(queryParameters)
            };

            HttpResponseMessage response = await client.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            _settings.AccessToken = JsonConvert.DeserializeObject<Token>(responseContent);

            string organizationAccessUrl = await NeedsOrganizationAccess();
            if (organizationAccessUrl != null)
            {
                return Redirect(organizationAccessUrl);
            }

            ViewBag.Settings = _settings;

            return View("Index");
        }

        [HttpPost]
        [Route("/call-api")]
        public async Task<IActionResult> CallAPI(string url, string AccessToken)
        {
            var response = await SecuredApiGetRequest(url, AccessToken);

            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            var jsonResponse = JsonConvert.DeserializeObject(responseBody);

            ViewBag.APIResponse = JsonConvert.SerializeObject(jsonResponse, Formatting.Indented);
            ViewBag.Settings = _settings;

            return View("Index");
        }

        [Route("/refresh-access-token")]
        public async Task<IActionResult> RefreshAccessToken()
        {
            Dictionary<string, object> oAuthMetadata = await GetOAuthMetadata(_settings.WellKnown);
            string tokenEndpoint = oAuthMetadata["token_endpoint"].ToString();

            var queryParameters = new Dictionary<string, string>();
            queryParameters.Add("grant_type", "refresh_token");
            queryParameters.Add("refresh_token", _settings.AccessToken.refresh_token);
            queryParameters.Add("redirect_uri", _settings.CallbackUrl);

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("authorization", $"Basic {GetBase64EncodedClientCredentials()}");

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(queryParameters)
            };

            HttpResponseMessage response = await client.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            _settings.AccessToken = JsonConvert.DeserializeObject<Token>(responseContent);

            ViewBag.Settings = _settings;

            return View("Index");
        }

        private string GetBase64EncodedClientCredentials()
        {
            byte[] credentialBytes = Encoding.UTF8.GetBytes($"{_settings.ClientId}:{_settings.ClientSecret}");
            return Convert.ToBase64String(credentialBytes);
        }

        private static async Task<Dictionary<string, object>> GetOAuthMetadata(string WellKnown)
        {
            HttpClient client = new HttpClient();
            var response = await client.GetAsync(WellKnown);

            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            var oAuthMetadata = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseContent);
            return oAuthMetadata;
        }

        private async Task<HttpResponseMessage> SecuredApiGetRequest(string url, string atoken)
        {
            var client = new HttpClient();

            var token = atoken;
            if(_settings.AccessToken != null)
            {
                token = _settings.AccessToken.access_token;
            }
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.deere.axiom.v3+json"));
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            return await client.GetAsync(url);
        }

        /// <summary>Check to see if the 'connections' rel is present for any organization.
        /// If the rel is present it means the oauth application has not completed it's
        /// access to an organization and must redirect the user to the uri provided
        /// in the link.</summary>
        /// <returns>A redirect uri if the <code>connections</code>
        /// connections rel is present or <null> if no redirect is
        /// required to finish the setup.</returns>
        private async Task<string> NeedsOrganizationAccess()
        {
            var response = await SecuredApiGetRequest(_settings.APIURL + "/organizations", null);

            response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            var dynorg = JsonConvert.DeserializeObject<dynamic>(responseContent);

            foreach (var organization in dynorg.values)
            {
                foreach (var link in organization.links)
                {
                    string rel = link.rel;
                    if (rel == "connections")
                    {
                        string connectionsLink = link.uri;
                        return QueryHelpers.AddQueryString(connectionsLink, "redirect_uri", _settings.ServerUrl);
                    }
                }
            }
            return null;
        }
    }
}

