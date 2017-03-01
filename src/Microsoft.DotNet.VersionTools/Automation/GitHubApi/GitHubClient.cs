﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.DotNet.VersionTools.Automation.GitHubApi
{
    public class GitHubClient : IDisposable
    {
        private static JsonSerializerSettings s_jsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        private static readonly string[] s_rateLimitHeaderNames =
        {
            "X-RateLimit-Limit",
            "X-RateLimit-Remaining",
            "X-RateLimit-Reset"
        };

        private GitHubAuth _auth;

        private HttpClient _httpClient;

        public GitHubClient(GitHubAuth auth)
        {
            _auth = auth;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"token {auth.AuthToken}");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", auth.User);
        }

        public async Task<GitHubContents> GetGitHubFileAsync(
            string path,
            GitHubBranch branch)
        {
            string url = $"https://api.github.com/repos/{branch.Project.Segments}/contents/{path}?ref=heads/{branch.Name}";

            Trace.TraceInformation($"Getting contents of '{path}' using '{url}'");

            using (HttpResponseMessage response = await _httpClient.GetAsync(url))
            {
                return await DeserializeSuccessfulAsync<GitHubContents>(response);
            }
        }

        public async Task<string> GetGitHubFileContentsAsync(
            string path,
            GitHubBranch branch)
        {
            try
            {
                GitHubContents file = await GetGitHubFileAsync(path, branch);
                return FromBase64(file.Content);
            }
            catch (HttpFailureResponseException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task PutGitHubFileAsync(
            string fileUrl,
            string commitMessage,
            string newFileContents)
        {
            Trace.TraceInformation($"Getting the 'sha' of the current contents of file '{fileUrl}'");

            string currentFile = await _httpClient.GetStringAsync(fileUrl);
            string currentSha = JObject.Parse(currentFile)["sha"].ToString();

            Trace.TraceInformation($"Got 'sha' value of '{currentSha}'");

            Trace.TraceInformation($"Request to update file '{fileUrl}' contents to:");
            Trace.TraceInformation(newFileContents);

            string updateFileBody = JsonConvert.SerializeObject(new
            {
                message = commitMessage,
                committer = new
                {
                    name = _auth.User,
                    email = _auth.Email
                },
                content = ToBase64(newFileContents),
                sha = currentSha
            }, Formatting.Indented);

            var bodyContent = new StringContent(updateFileBody);
            using (HttpResponseMessage response = await _httpClient.PutAsync(fileUrl, bodyContent))
            {
                await EnsureSuccessfulAsync(response);
                Trace.TraceInformation("Updated the file successfully.");
            }
        }

        public async Task PostGitHubPullRequestAsync(
            string title,
            string description,
            GitHubBranch headBranch,
            GitHubBranch baseBranch)
        {
            string createPrBody = JsonConvert.SerializeObject(new
            {
                title = title,
                body = description,
                head = $"{headBranch.Project.Owner}:{headBranch.Name}",
                @base = baseBranch.Name
            }, Formatting.Indented);

            string pullUrl = $"https://api.github.com/repos/{baseBranch.Project.Segments}/pulls";

            var bodyContent = new StringContent(createPrBody);
            using (HttpResponseMessage response = await _httpClient.PostAsync(pullUrl, bodyContent))
            {
                await EnsureSuccessfulAsync(response);

                Trace.TraceInformation($"Created pull request.");
                Trace.TraceInformation($"Pull request page: {await GetPullRequestUrlAsync(response)}");
            }
        }

        public async Task UpdateGitHubPullRequestAsync(
            GitHubProject project,
            int number,
            string title = null,
            string body = null,
            string state = null)
        {
            var updatePrBody = new JObject();

            if (title != null)
            {
                updatePrBody.Add(new JProperty("title", title));
            }
            if (body != null)
            {
                updatePrBody.Add(new JProperty("body", body));
            }
            if (state != null)
            {
                updatePrBody.Add(new JProperty("state", state));
            }

            string url = $"https://api.github.com/repos/{project.Segments}/pulls/{number}";

            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("PATCH"), url);
            request.Content = new StringContent(JsonConvert.SerializeObject(updatePrBody));

            using (HttpResponseMessage response = await _httpClient.SendAsync(request))
            {
                await EnsureSuccessfulAsync(response);

                Trace.TraceInformation($"Updated pull request #{number}.");
                Trace.TraceInformation($"Pull request page: {await GetPullRequestUrlAsync(response)}");
            }
        }

        public async Task<GitHubPullRequest> SearchPullRequestsAsync(
            GitHubProject project,
            string headPrefix,
            string author,
            string sortType = "created")
        {
            int pullRequestNumber;

            // First: find the number of the pull request.
            string queryString = $"repo:{project.Segments}+head:{headPrefix}+author:{author}+state:open";
            string queryUrl = $"https://api.github.com/search/issues?q={queryString}&sort={sortType}&order=desc";

            using (HttpResponseMessage response = await _httpClient.GetAsync(queryUrl))
            {
                await EnsureSuccessfulAsync(response);

                var queryResponse = JsonConvert.DeserializeObject<GitHubIssueQueryResponse>(
                    await response.Content.ReadAsStringAsync(),
                    s_jsonSettings);

                if (queryResponse.TotalCount == 0)
                {
                    Trace.TraceInformation($"Could not find any pull request with head {headPrefix}");
                    return null;
                }
                if (queryResponse.TotalCount > 1)
                {
                    IEnumerable<int> allIds = queryResponse.Items.Select(item => item.Id);
                    Trace.TraceInformation($"Found multiple pull requests with head {headPrefix}. On this page, found {string.Join(", ", allIds)}");
                }

                pullRequestNumber = queryResponse.Items.First().Number;
            }
            // Second: fetch details for the pull request.
            string pullRequestUrl = $"https://api.github.com/repos/{project.Segments}/pulls/{pullRequestNumber}";

            using (HttpResponseMessage response = await _httpClient.GetAsync(pullRequestUrl))
            {
                return await DeserializeSuccessfulAsync<GitHubPullRequest>(response);
            }
        }

        public async Task PostCommentAsync(GitHubProject project, int issueNumber, string message)
        {
            string commentBody = JsonConvert.SerializeObject(new
            {
                body = message
            });

            string url = $"https://api.github.com/repos/{project.Segments}/issues/{issueNumber}/comments";

            var bodyContent = new StringContent(commentBody);
            using (HttpResponseMessage response = await _httpClient.PostAsync(url, bodyContent))
            {
                await EnsureSuccessfulAsync(response);
            }
        }

        public async Task<GitCommit> GetCommitAsync(GitHubProject project, string sha)
        {
            string url = $"https://api.github.com/repos/{project.Segments}/git/commits/{sha}";

            using (HttpResponseMessage response = await _httpClient.GetAsync(url))
            {
                Trace.TraceInformation($"Getting info about commit {sha} in {project.Segments}");
                return await DeserializeSuccessfulAsync<GitCommit>(response);
            }
        }

        public async Task<GitReference> GetReferenceAsync(GitHubProject project, string @ref)
        {
            string url = $"https://api.github.com/repos/{project.Segments}/git/refs/{@ref}";

            using (HttpResponseMessage response = await _httpClient.GetAsync(url))
            {
                Trace.TraceInformation($"Getting info about ref {@ref} in {project.Segments}");
                return await DeserializeSuccessfulAsync<GitReference>(response);
            }
        }

        public async Task<GitTree> PostTreeAsync(GitHubProject project, string baseTree, GitObject[] tree)
        {
            string body = JsonConvert.SerializeObject(new
            {
                base_tree = baseTree,
                tree
            }, Formatting.Indented, s_jsonSettings);

            string url = $"https://api.github.com/repos/{project.Segments}/git/trees";

            var bodyContent = new StringContent(body);
            using (HttpResponseMessage response = await _httpClient.PostAsync(url, bodyContent))
            {
                Trace.TraceInformation($"Posting new tree to {project.Segments}");
                return await DeserializeSuccessfulAsync<GitTree>(response);
            }
        }

        public async Task<GitCommit> PostCommitAsync(
            GitHubProject project,
            string message,
            string tree,
            string[] parents)
        {
            string body = JsonConvert.SerializeObject(new
            {
                message,
                tree,
                parents
            }, Formatting.Indented);

            string url = $"https://api.github.com/repos/{project.Segments}/git/commits";

            var bodyContent = new StringContent(body);
            using (HttpResponseMessage response = await _httpClient.PostAsync(url, bodyContent))
            {
                Trace.TraceInformation($"Posting new commit for tree '{tree}' with parents '{string.Join(", ", parents)}' to {project.Segments}");
                return await DeserializeSuccessfulAsync<GitCommit>(response);
            }
        }

        public async Task<GitReference> PatchReferenceAsync(GitHubProject project, string @ref, string sha, bool force)
        {
            string body = JsonConvert.SerializeObject(new
            {
                sha,
                force
            }, Formatting.Indented);

            string url = $"https://api.github.com/repos/{project.Segments}/git/refs/{@ref}";

            var bodyContent = new StringContent(body);
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
            {
                Content = bodyContent
            };
            using (HttpResponseMessage response = await _httpClient.SendAsync(request))
            {
                Trace.TraceInformation($"Patching reference '{@ref}' to '{sha}' with force={force} in {project.Segments}");
                return await DeserializeSuccessfulAsync<GitReference>(response);
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        private static async Task<T> DeserializeSuccessfulAsync<T>(HttpResponseMessage response)
        {
            await EnsureSuccessfulAsync(response);

            return JsonConvert.DeserializeObject<T>(
                await response.Content.ReadAsStringAsync(),
                s_jsonSettings);
        }

        private static async Task EnsureSuccessfulAsync(HttpResponseMessage response)
        {
            foreach (string headerName in s_rateLimitHeaderNames)
            {
                IEnumerable<string> headerValues;
                if (response.Headers.TryGetValues(headerName, out headerValues))
                {
                    Trace.TraceInformation($"{headerName}: {string.Join(", ", headerValues)}");
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                string failureContent = await response.Content.ReadAsStringAsync();
                string message = $"Response code does not indicate success: {(int)response.StatusCode} ({response.StatusCode})";
                if (!string.IsNullOrWhiteSpace(failureContent))
                {
                    message += $" with content: {failureContent}";
                }
                throw new HttpFailureResponseException(response.StatusCode, message);
            }
        }

        private static async Task<string> GetPullRequestUrlAsync(HttpResponseMessage response)
        {
            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());
            return responseContent["html_url"].ToString();
        }

        private static string ToBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        private static string FromBase64(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}