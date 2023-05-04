using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Html.Parser;
using Spectre.Console;

internal static class XmlParser
{
    private static readonly Regex DisqusUserRegex = new(@"@([\w_\-0-9]+)\:disqus");

    public static List<DisqusBlogPost> Parse(
        string path,
        List<BlogPost> validUrls,
        Dictionary<long, bool> forceIncludeComments,
        Dictionary<string, string> disqusToGitHubUserMap,
        string myDisqusAccount)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        var namespaceManager = new XmlNamespaceManager(doc.NameTable);
        namespaceManager.AddNamespace(String.Empty, "http://disqus.com");
        namespaceManager.AddNamespace("def", "http://disqus.com");
        namespaceManager.AddNamespace("dsq", "http://disqus.com/disqus-internals");

        var posts = FindPosts(doc, namespaceManager);
        var comments = FindComments(doc, namespaceManager, forceIncludeComments, disqusToGitHubUserMap, myDisqusAccount);
        FixCommentMessage(comments);

        Logger.Log($"[cyan1]{posts.Count} valid posts found[/]");
        Logger.Log($"[cyan1]{comments.Count} valid comments found[/]");
        var buildTree = BuildTree(comments, posts, validUrls);

        if (Logger.VerboseEnabled)
        {
            PrintTree(buildTree);
        }

        return buildTree;
    }

    static IDictionary<long, DisqusBlogPost> FindPosts(XmlDocument doc, XmlNamespaceManager namespaceManager)
    {
        var postNodes = doc.DocumentElement!.SelectNodes("def:thread", namespaceManager)!;

        var posts = new Dictionary<long, DisqusBlogPost>();
        var i = 0;
        foreach (XmlNode postNode in postNodes)
        {
            i++;

            long postId = long.Parse(postNode.Attributes!.Item(0)!.Value!);
            var title = postNode["title"]?.InnerText?.Trim();
            var url = postNode["link"]?.InnerText;

            Logger.Verbose($"{i:###} Found post ({postId}) '{title.EscapeMarkup()}' for {url.EscapeMarkup()}");
            if (bool.Parse(postNode["isDeleted"]!.InnerText!))
            {
                Logger.Verbose($"  [orange3]{i:###} Post ({postId}) was deleted.[/]");
                continue;
            }

            if (bool.Parse(postNode["isClosed"]!.InnerText!))
            {
                Logger.Verbose($"  [orange3]{i:###} Post ({postId}) was closed.[/]");
                continue;
            }

            var createdAt = DateTime.Parse(postNode["createdAt"]!.InnerText!);
            posts.Add(postId, new DisqusBlogPost(postId, title, url, createdAt));
        }

        return posts;
    }

    static IDictionary<long, DisqusComment> FindComments(
        XmlDocument doc,
        XmlNamespaceManager namespaceManager,
        Dictionary<long, bool> forceIncludeComments,
        Dictionary<string, string> disqusToGitHubUserMap,
        string myDisqusAccount)
    {
        var commentNodes = doc.DocumentElement.SelectNodes("def:post", namespaceManager);
        var comments = new Dictionary<long, DisqusComment>();
        var i = 0;
        foreach (XmlNode commentNode in commentNodes)
        {
            i++;
            long commentId = long.Parse(commentNode.Attributes!.Item(0)!.Value!);

            var authorNode = commentNode["author"]!;
            var name = authorNode["name"]?.InnerText ?? "Anonymous";
            var username = authorNode["username"]?.InnerText;
            var isAnonymous = bool.Parse(authorNode["isAnonymous"]!.InnerText);
            var githubUser = disqusToGitHubUserMap.TryGetValue(username ?? "", out var githubUserName) ? githubUserName : null;
            var author = new Author(name, username, githubUser, isAnonymous, username == myDisqusAccount);

            Logger.Verbose($"{i:###} Found comment ({commentId}) by '{name.EscapeMarkup()}'");

            bool forceInclude = false;
            if (forceIncludeComments.TryGetValue(commentId, out var value))
            {
                if (!value)
                {
                    Logger.Verbose($"  [orange3]{i:###} Comment ({commentId}) was force excluded.[/]");
                    continue;
                }

                forceInclude = true;
            }

            if (bool.Parse(commentNode["isDeleted"]!.InnerText) && !forceInclude)
            {
                Logger.Verbose($"  [orange3]{i:###} Comment ({commentId}) was deleted.[/]");
                continue;
            }

            if (bool.Parse(commentNode["isSpam"]!.InnerText!) && !forceInclude)
            {
                Logger.Verbose($"  [orange3]{i:###} Comment ({commentId}) was marked as spam.[/]");
                continue;
            }

            var message = (commentNode["message"]!.FirstChild as XmlCDataSection)!.Value;
            var postId = long.Parse(commentNode["thread"]!.Attributes!.Item(0)!.Value!);
            long? parentId = commentNode["parent"]?.Attributes.Item(0)!.Value is { } p ? long.Parse(p) : null;
            var createdAt = DateTime.Parse(commentNode["createdAt"]!.InnerText!);

            var post = new DisqusComment(commentId, postId, parentId, createdAt, author)
            {
                Message = message,
            };
            comments.Add(commentId, post);
        }

        return comments;
    }

    static List<DisqusBlogPost> BuildTree(IDictionary<long, DisqusComment> comments, IDictionary<long, DisqusBlogPost> posts, List<BlogPost> validUrls)
    {
        Logger.Log($"Adding top-level comments to posts...");
        foreach (var comment in comments.Values.Where(x => x.ParentId is null))
        {
            var post = posts[comment.PostId];
            post.Comments.Add(comment);
        }

        Logger.Log($"Adding child comments...");
        foreach (var comment in comments.Values.Where(x => x.ParentId is not null))
        {
            try
            {
                var parent = comments[comment.ParentId!.Value];
                if (parent.ParentId.HasValue)
                {
                    Logger.Verbose($"Re-parenting comment {comment.Id}...");
                    while (parent.ParentId.HasValue)
                    {
                        parent = comments[parent.ParentId.Value];
                    }
                }

                parent.ChildComments.Add(comment);
            }
            catch (Exception)
            {
                Logger.Log($"Error adding child comment {comment.Id} to parent {comment.ParentId}");
                throw;
            }
        }

        var finalList = posts.Values
            .Where(x =>
            {
                if (string.IsNullOrEmpty(x.Url))
                {
                    Logger.Log($"[red] The url for Post ({x.Id}) is not valid: {x.Url.EscapeMarkup()}[/]");
                    return false;
                }

                if (x.Comments.Count == 0)
                {
                    Logger.Verbose($"[red] Post ({x.Id}) has no comments {x.Url.EscapeMarkup()}[/]");
                    return false;
                }

                return true;
            })
            .Where(x =>
            {
                var matchingPost = validUrls.FirstOrDefault(post => string.Equals(post.Url, x.Url, StringComparison.OrdinalIgnoreCase));
                if (matchingPost is not null)
                {
                    x.MatchingPost = matchingPost;
                    return true;
                }

                Logger.Log($"[red] No matching blog post for DisqusPost ({x.Id}): {x.Url.EscapeMarkup()}[/]");
                return false;
            })
            .ToList();

        Logger.Log($"[cyan1]Retained {finalList.Count} posts[/]");
        return finalList;
    }

    static void PrintTree(IEnumerable<DisqusBlogPost> posts)
    {
        void RecurseChildren(IHasTreeNodes node, List<DisqusComment> children)
        {
            foreach (DisqusComment child in children)
            {
                IHasTreeNodes parent = node.AddNode($"[yellow]Comment by {child.Author.Username ?? "Anonymous"}[/]");

                RecurseChildren(parent, child.ChildComments);
            }
        }

        var tree = new Tree(string.Empty);
        foreach (var post in posts)
        {
            var node = tree.AddNode($"{post.Url.EscapeMarkup()} ({post.Sha1()})");
            RecurseChildren(node, post.Comments);
        }

        AnsiConsole.Render(tree);
    }

    private static void FixCommentMessage(IDictionary<long, DisqusComment> comments)
    {
        var htmlParser = new HtmlParser();
        var authors = comments.Values
            .Select(x => x.Author)
            .DistinctBy(x => x.Username)
            .ToDictionary(x => x.Username!, x => x);

        foreach (var comment in comments.Values)
        {
            // fix the authors
            if (DisqusUserRegex.Match(comment.Message) is {Success: true, Groups: {Count: 2} groups})
            {
                var username = groups[1].Value;
                var author = authors.TryGetValue(username, out var known)
                    ? known.AuthorAnchor
                    : username; // may be a deleted user we don't know about

                comment.Message = DisqusUserRegex.Replace(comment.Message, author);
            }

            // fix the anchors
            var message = htmlParser.ParseDocument($"<html><body>{comment.Message}</body></html>");
            foreach (var anchor in message.QuerySelectorAll("a").Where(x => x.InnerHtml.Length > 3))
            {
                var href = anchor.Attributes["href"]?.Value;
                var innerHtml = anchor.InnerHtml; 
                var subIndexHtml = innerHtml.Substring(0, innerHtml.Length - 3);
                if (href is not null && href.StartsWith(subIndexHtml))
                {
                    anchor.InnerHtml = href;
                }
            }

            comment.Message = message.Body.InnerHtml;
        }
    }
}