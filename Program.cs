using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Atlassian.Jira;
using Octokit;
using RestSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jira_to_GitHub
{
    static class Program
    {
        public const string RepoOwnerName = "tryashtar";
        public const string RepoName = "minecraft-bugs";
        public const string CurrentVersion = "1.12.2";
        public const string AppDescription = "tryashtar-jira-to-github-mirror";
        static Dictionary<string, JiraAuthor> UserCache = new Dictionary<string, JiraAuthor>();
        static List<DualTicket> LocalGitHub;
        static GitHubClient GitHub = new GitHubClient(new ProductHeaderValue(AppDescription));
        static Jira Mojira;

        public static string[] ImageExtensions()
        {
            return new string[] { ".png", ".jpg", ".jpeg" };
        }

        // get the user's info from the cache
        // if this is a new user, fetch its info and add it to the cache 
        static JiraAuthor GetUser(string username)
        {
            if (!UserCache.TryGetValue(username, out var user))
            {
                var request = new RestRequest("/rest/api/2/user", Method.GET).AddParameter("username", username);
                var result = JObject.Parse(Mojira.RestClient.RestSharpClient.Execute(request).Content);
                user = new JiraAuthor(username, (string)result["displayName"], (string)result["avatarUrls"]["24x24"]);
                UserCache.Add(username, user);
            }
            return user;
        }

        // make a request until it succeeds, printing wait information to the console
        // must be "awaited"
        // note that if you make an invalid request (like deleting a comment that doesn't exist), it will try again and again forever
        static async Task<dynamic> GitHubDemand(GitHubRequestType request, NewIssue newissue = null, IssueUpdate issueupdate = null, int number = -1, int commentid = -1, string comment = null)
        {
            while (true)
            {
                try
                {
                    switch (request)
                    {
                        case GitHubRequestType.CreateIssue:
                            return await GitHub.Issue.Create(RepoOwnerName, RepoName, newissue);
                        case GitHubRequestType.UpdateIssue:
                            return await GitHub.Issue.Update(RepoOwnerName, RepoName, number, issueupdate);
                        case GitHubRequestType.LockIssue:
                            await GitHub.Issue.Lock(RepoOwnerName, RepoName, number);
                            return null;
                        case GitHubRequestType.CreateComment:
                            return await GitHub.Issue.Comment.Create(RepoOwnerName, RepoName, number, comment);
                        case GitHubRequestType.UpdateComment:
                            return await GitHub.Issue.Comment.Update(RepoOwnerName, RepoName, commentid, comment);
                        case GitHubRequestType.DeleteComment:
                            await GitHub.Issue.Comment.Delete(RepoOwnerName, RepoName, commentid);
                            return null;
                        default:
                            return null;
                    }
                }
                catch (Octokit.ApiException ex)
                {
                    int seconds = 5;
                    if (ex is AbuseException abex)
                        seconds = abex.RetryAfterSeconds ?? 100;
                    else if (ex is RateLimitExceededException rlex)
                        seconds = (rlex.Reset - DateTimeOffset.Now).Seconds;
                    Console.WriteLine("   Error: " + ex.Message);
                    Console.WriteLine("   Waiting " + seconds + " seconds...");
                    Console.Write("   ");
                    // break our waiting progress into 20 messages
                    // if waiting less than 20 seconds, use less (1 sec per message)
                    // if waiting more than 100 seconds, use more (5 sec per message)
                    int remove = (int)Math.Max(1, Math.Min(5, Math.Ceiling(seconds / 20d)));
                    while (seconds > 0)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(remove));
                        Console.Write(seconds + " ");
                        seconds -= remove;
                    }
                    Console.WriteLine();
                }
            }
        }

        public static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            // get credentials
            Console.WriteLine("Mojira username:");
            string muser = Console.ReadLine();
            Console.WriteLine("Mojira password:");
            string mpass = Console.ReadLine();
            Console.Clear();
            Console.WriteLine("GitHub username:");
            string guser = Console.ReadLine();
            Console.WriteLine("GitHub password:");
            string gpass = Console.ReadLine();
            Console.Clear();

            Mojira = Jira.CreateRestClient("https://bugs.mojang.com", muser, mpass);
            Mojira.Issues.MaxIssuesPerRequest = 1000;
            GitHub.Credentials = new Credentials(guser, gpass);
            LocalGitHub = JsonConvert.DeserializeObject<List<DualTicket>>(File.ReadAllText("issues.json")) ?? new List<DualTicket>();
            Console.WriteLine($"Found {LocalGitHub.Count} local tickets");
            DateTime start = DateTime.Now;

            int searchindex = 0;
            int totaltickets = 0;
            do
            {
                Console.WriteLine("Grabbing a new batch of jira tickets...");
                var ticketbatch = await Mojira.Issues.GetIssuesFromJqlAsync($"project = MC ORDER BY created ASC", 1000, searchindex);
                totaltickets = ticketbatch.TotalItems;
                searchindex += ticketbatch.ItemsPerPage;
                // fetch and process 1000 tickets at a time
                foreach (var jiraticket in ticketbatch)
                {
                    Console.WriteLine($"Fetching {jiraticket.Key.Value}: {jiraticket.Summary}");
                    var reporter = GetUser(jiraticket.Reporter);
                    var comments = new List<Tuple<JiraAuthor, Comment>>();
                    foreach (var comment in await jiraticket.GetCommentsAsync())
                    {
                        var author = GetUser(comment.Author);
                        comments.Add(Tuple.Create(author, comment));
                    }
                    var attachments = await jiraticket.GetAttachmentsAsync();
                    Console.WriteLine("   Fetch complete, sending to github...");

                    // the ticket we just fetched from mojira
                    DualTicket newticket = new DualTicket(jiraticket, reporter, comments, attachments);
                    // our old version of that ticket (or "null" if the ticket is new)
                    DualTicket currentticket = LocalGitHub.FirstOrDefault(x => x.JiraKey == newticket.JiraKey);
                    bool needssave = true;

                    if (currentticket == null) // new ticket: send it to github
                    {
                        newticket.GitHubNumber = (await GitHubDemand(GitHubRequestType.CreateIssue, newissue: newticket.ToGitHubNew())).Number;
                        foreach (var comment in newticket.Comments)
                        {
                            comment.GitHubID = (await GitHubDemand(GitHubRequestType.CreateComment, number: newticket.GitHubNumber, comment: comment.Description)).Id;
                        }
                        // close the ticket with an update if necessary
                        if (!newticket.Open)
                            await GitHubDemand(GitHubRequestType.UpdateIssue, number: newticket.GitHubNumber, issueupdate: newticket.ToGitHubUpdate());
                        await GitHubDemand(GitHubRequestType.LockIssue, number: newticket.GitHubNumber);
                        LocalGitHub.Add(newticket);
                        Console.WriteLine($"   Sent new issue to github (#{newticket.GitHubNumber} with {newticket.Comments.Count} comments)");
                    }
                    else if (!currentticket.ContentMatches(newticket)) // updated ticket: send changes
                    {
                        if (!currentticket.TicketMatches(newticket))
                        {
                            await GitHubDemand(GitHubRequestType.UpdateIssue, issueupdate: newticket.ToGitHubUpdate(), number: currentticket.GitHubNumber);
                            Console.WriteLine($"   Updated issue #{currentticket.GitHubNumber} on github");
                        }
                        int added = 0;
                        int updated = 0;
                        int deleted = 0;
                        // post new comments and update existing ones
                        foreach (var comment in newticket.Comments)
                        {
                            var existing = currentticket.Comments.FirstOrDefault(x => x.JiraID == comment.JiraID);
                            if (existing == null) // new comment: send it to github
                            {
                                comment.GitHubID = (await GitHubDemand(GitHubRequestType.CreateComment, number: currentticket.GitHubNumber, comment: comment.Description)).Id;
                                added++;
                            }
                            else if (comment.Description != existing.Description) // updated comment: send changes
                            {
                                await GitHubDemand(GitHubRequestType.UpdateComment, commentid: existing.GitHubID, comment: comment.Description);
                                updated++;
                            }
                            // else, no new changes need to be made
                        }
                        // delete comments that were deleted on mojira
                        foreach (var comment in currentticket.Comments)
                        {
                            if (!newticket.Comments.Any(x => x.JiraID == comment.JiraID))
                            {
                                await GitHubDemand(GitHubRequestType.DeleteComment, commentid: comment.GitHubID);
                                deleted++;
                            }
                        }
                        if (added > 0)
                            Console.WriteLine($"   Added {added} new comments");
                        if (updated > 0)
                            Console.WriteLine($"   Updated {updated} existing comments");
                        if (deleted > 0)
                            Console.WriteLine($"   Removed {deleted} deleted comments");
                        newticket.GitHubNumber = currentticket.GitHubNumber;
                        currentticket.SetTo(newticket);
                    }
                    else // no new changes need to be made
                    {
                        needssave = false;
                        Console.WriteLine("   Already up-to-date");
                    }
                    if (needssave)
                        File.WriteAllText("issues.json", JsonConvert.SerializeObject(LocalGitHub));
                }
            } while (searchindex < totaltickets);
            Console.WriteLine($"Finished successfully after {(DateTime.Now - start).TotalMinutes} minutes");
            Console.ReadLine();
        }
    }

    enum GitHubRequestType
    {
        CreateIssue,
        UpdateIssue,
        LockIssue,
        CreateComment,
        UpdateComment,
        DeleteComment
    }

    class DualTicket
    {
        public string JiraKey = null;
        public int GitHubNumber = -1;
        public string Title = "";
        public string Description = "";
        public bool Open = true;
        public List<DualComment> Comments = new List<DualComment>();
        public List<string> Labels = new List<string>();

        public DualTicket()
        { }

        public DualTicket(Atlassian.Jira.Issue jiraticket, JiraAuthor reporter, IEnumerable<Tuple<JiraAuthor, Comment>> comments, IEnumerable<Attachment> attachments)
        {
            JiraKey = jiraticket.Key.Value;
            Title = jiraticket.Summary;
            Description = $"## [Mojira Ticket {jiraticket.Key.Value}](https://bugs.mojang.com/browse/{jiraticket.Key.Value})\n### <img src=\"{reporter.AvatarURL}\" width=20 height=20> [{reporter.DisplayName}](https://bugs.mojang.com/secure/ViewProfile.jspa?name={Uri.EscapeUriString(reporter.Username)})" + (jiraticket.Created.HasValue ? $" • {jiraticket.Created.Value.ToString("MMM d, yyyy")}" : "") + $"\n\n{ProcessDescription(jiraticket.Description)}";
            Open = !(jiraticket.Status.Name == "Closed" || jiraticket.Status.Name == "Resolved");
            if (attachments.Any())
            {
                Description += "\n### Attachments:\n";
                foreach (var attachment in attachments)
                {
                    string url = $"https://bugs.mojang.com/secure/attachment/{attachment.Id}/{Uri.EscapeUriString(attachment.FileName)}";
                    if (Program.ImageExtensions().Contains(Path.GetExtension(attachment.FileName)))
                        Description += $"  <img src=\"{url}\" width=\"240\" height=\"135\">";
                    else
                        Description += $"  \n[{attachment.FileName}]({url})";
                }
            }
            foreach (var version in jiraticket.AffectsVersions)
            {
                if (version.Name == "Minecraft " + Program.CurrentVersion)
                    Labels.Add("affects " + Program.CurrentVersion);
            }
            if (jiraticket.Resolution?.Name == "Fixed")
                Labels.Add("fixed");
            else if (jiraticket.Resolution?.Name == "Duplicate")
                Labels.Add("duplicate");
            else if (jiraticket.Resolution?.Name == "Invalid" || jiraticket.Resolution?.Name == "Cannot Reproduce")
                Labels.Add("invalid");
            else if (jiraticket.Resolution?.Name == "Won't Fix")
                Labels.Add("won't fix");
            else if (jiraticket.Resolution?.Name == "Works As Intended")
                Labels.Add("works as intended");
            foreach (var author_comment in comments)
            {
                Comments.Add(new DualComment(jiraid: author_comment.Item2.Id, description: $"### <img src=\"{author_comment.Item1.AvatarURL}\" width=20 height=20> [{author_comment.Item1.DisplayName}](https://bugs.mojang.com/browse/{jiraticket.Key.Value}?focusedCommentId={author_comment.Item2.Id}#comment-{author_comment.Item2.Id})" + (author_comment.Item2.CreatedDate.HasValue ? $" • {author_comment.Item2.CreatedDate.Value.ToString("MMM d, yyyy")}" : "") + $"\n{ProcessDescription(author_comment.Item2.Body)}"));
            }
        }

        public DualTicket(Octokit.Issue githubissue, IEnumerable<IssueComment> comments)
        {
            GitHubNumber = githubissue.Number;
            Title = githubissue.Title;
            Description = githubissue.Body;
            Open = githubissue.State.Value == ItemState.Open;
            Labels.AddRange(githubissue.Labels.Select(x => x.Name));
            foreach (var comment in comments)
            {
                if (comment.User.Login == Program.RepoOwnerName)
                    Comments.Add(new DualComment(githubid: comment.Id, description: comment.Body));
            }
        }

        public NewIssue ToGitHubNew()
        {
            NewIssue issue = new NewIssue(Title);
            issue.Body = Description;
            foreach (string label in Labels)
            {
                issue.Labels.Add(label);
            }
            return issue;
        }

        public IssueUpdate ToGitHubUpdate()
        {
            IssueUpdate update = new IssueUpdate();
            update.Title = Title;
            update.Body = Description;
            update.State = Open ? ItemState.Open : ItemState.Closed;
            foreach (string label in Labels)
            {
                update.AddLabel(label);
            }
            return update;
        }

        public void SetTo(DualTicket other)
        {
            if (this == other)
                return;
            this.Title = other.Title;
            this.Description = other.Description;
            this.Open = other.Open;
            Labels.Clear();
            Labels.AddRange(other.Labels);
        }

        public bool TicketMatches(DualTicket other)
        {
            if (this == other)
                return true;
            return this.Title == other.Title &&
                this.Description == other.Description &&
                this.Open == other.Open &&
                this.Labels.SequenceEqual(other.Labels);
        }

        public bool ContentMatches(DualTicket other)
        {
            return TicketMatches(other) && this.Comments.Select(x => x.Description).SequenceEqual(other.Comments.Select(x => x.Description));
        }

        // simple now (just prevents github pings)
        // in the future ideally this would convert jira edit syntax to github markdown
        private string ProcessDescription(string description)
        {
            if (description == null)
                return "";
            return description.Replace("@", "(at)");
        }
    }

    class DualComment
    {
        public string JiraID = null;
        public int GitHubID = -1;
        public string Description = "";
        public DualComment()
        { }
        public DualComment(string jiraid = null, int githubid = -1, string description = "")
        {
            JiraID = jiraid;
            GitHubID = githubid;
            Description = description;
        }
    }

    class JiraAuthor
    {
        public string Username;
        public string DisplayName;
        public string AvatarURL;
        public JiraAuthor(string user, string display, string url)
        {
            Username = user;
            DisplayName = display;
            AvatarURL = url;
        }
    }
}
