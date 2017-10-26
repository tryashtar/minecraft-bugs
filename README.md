## Minecraft Java Edition Bug Tracker

I whipped up a bot to download all the issues from https://bugs.mojang.com and send them here. Every comment and attachment on each ticket is sent as well, plus relevant labels. As tickets change and comments are added, updated, or deleted, the bot does likewise here.

It's a very slow-going process; Jira's API is very kind and lets the bot fetch issues pretty quickly without any real rate-limiting, but GitHub is a lot more harsh, only allowing the bot to post a few issues/comments at a time. Once everything is sent over, updating is relatively quick, and unchanged tickets are simply ignored.

After an issue is sent to GitHub, a copy of all the ticket's information is saved to my computer. Every local ticket has the Mojira key (such as MC-791) and the GitHub issue number (such as #752), so it knows where it came from and where it's going. IDs are also saved for comments, both on the Mojira and GitHub ends. This way, the bot doesn't have to ever *get* information from GitHub, only send, since the local tickets should always match what's currently on GitHub. This optimizes updating, as we can compare the new ticket downloaded from Jira to the local ticket. The process looks something like this:

1. Download an entire issue from Mojira.
2. Convert it to a common format that has title, description, labels, and comments. Information like reporter and attachments are merged into the description.
3. Check if we've downloaded this ticket before and sent it to GitHub.
4. If we have, check for differences. Update the title, description, labels, and comments if necessary.
5. If we haven't, send the new issue to GitHub. Save the ticket and link (Mojira Key and GitHub ID).

### Libraries
Thanks to Federico Silva Armas for [Atlassian.Net SDK](https://bitbucket.org/farmas/atlassian.net-sdk/wiki/Home)  
Thanks to GitHub for [octokit.net](https://github.com/octokit/octokit.net)  
Thanks to the [RestSharp](http://restsharp.org) developers  
Thanks to James Newton-King for [Json.NET](https://www.newtonsoft.com/json)
