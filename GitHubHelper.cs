using System.Diagnostics;
using Octokit.GraphQL;
using Octokit.GraphQL.Model;
using Spectre.Console;
using Environment = System.Environment;

internal class GitHubHelper
{
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly string _repo;
    private readonly Connection _mainConnection;
    private readonly Connection _botConnection;

    public GitHubHelper(string repoOwner, string repoName, string mainToken, string botToken)
    {
        _repoOwner = repoOwner;
        _repoName = repoName;
        _repo = $"{repoOwner}/{repoName}";
        _mainConnection = new Connection(new ProductHeaderValue("DisqusToGiscusConverter", "1.0.0"), mainToken);
        _botConnection = new Connection(new ProductHeaderValue("DisqusToGiscusConverter", "1.0.0"), botToken);
    }

    public async Task AssociateDiscussions(List<DisqusBlogPost> posts)
    {
        await PrintRateLimit(_mainConnection, "'me'");
        await PrintRateLimit(_mainConnection, "'bot'");
        var repoId = await GetRepositoryId(_mainConnection);
        var categoryId = await GetAnnouncementCategoryId(_mainConnection);

        var discussions = await GetAllDiscussions(_mainConnection, categoryId);
        Logger.Log($"[red]Fetched {discussions.Count} discussions [/]");

        foreach (var post in posts.Where(x => x.GithubDiscussion is null))
        {
            try
            {
                var discussion = GetDiscussion(discussions, post)
                                 // have to use main connection, as bot connection doesn't have permission 
                                 ?? await CreateDiscussion(_mainConnection, post, repoId, categoryId);
                post.GithubDiscussion = discussion;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error fetching/creating discussion for post {post.Title.EscapeMarkup()}: " + ex.Message);
                throw;
            }
        }

        Logger.Log("[cyan1]Matched up discussions successfully[/]");
    }

    public async Task AddComments(List<DisqusBlogPost> postsAndComments, Action checkpoint)
    {
        await PrintRateLimit(_mainConnection, "'me'");
        await PrintRateLimit(_mainConnection, "'bot'");

        foreach (var postAndComments in postsAndComments)
        {
            Logger.Log($"[cyan]Adding comments for {postAndComments.Title.EscapeMarkup()}[/]");
            var discussion = postAndComments.GithubDiscussion;
            Debug.Assert(discussion is not null);

            foreach (var topLevelComment in postAndComments.Comments)
            {
                await CreateComment(topLevelComment, discussion, postAndComments, replyTo: null);
                foreach (var childComment in topLevelComment.ChildComments)
                {
                    await CreateComment(childComment, discussion, postAndComments, replyTo: topLevelComment);
                }
            }
        }

        async Task CreateComment(DisqusComment comment, DiscussionSummary discussion, DisqusBlogPost postAndComments, DisqusComment? replyTo)
        {
            try
            {
                if (comment.GithubComment is null)
                {
                    comment.GithubComment = await CreateDiscussionComment(discussion, comment, replyTo);
                    checkpoint();
                        // adding a brief pause to avoid hitting abuse rate-limits
                    // https://github.com/cli/cli/issues/4801
                    await Task.Delay(2_500);
                }
            }
            catch (Exception)
            {
                Logger.Log($"Error adding top-level comment from {comment.Author.Username} to {postAndComments.Title.EscapeMarkup()}");
                throw;
            }
        }
    }

    async Task<ID> GetRepositoryId(Connection connection)
    {
        try
        {
            var query = new Query()
                .Repository(name: _repoName, owner: _repoOwner)
                .Select(x => x.Id)
                .Compile();

            return await connection.Run(query);
        }
        catch (Exception)
        {
            Logger.Log("[red]Error fetching repo ID[/]");
            throw;
        }
    }

    async Task PrintRateLimit(Connection connection, string connectionName)
    {
        try
        {
            var query = new Query()
                .RateLimit()
                .Select(x => new
                {
                    x.Limit,
                    x.Remaining,
                    x.ResetAt
                })
                .Compile();

            var results = await connection.Run(query);
            Logger.Log($"The {connectionName} connection currently has [cyan]{results.Remaining}[/] of [cyan]{results.Limit}[/]. Resets at {results.ResetAt:T} ");
        }
        catch (Exception)
        {
            Logger.Log("[red]Error fetching rate limit[/]");
        }
    }

    async Task<ID> GetAnnouncementCategoryId(Connection connection)
    {
        try
        {
            var query = new Query()
                .Repository(name: _repoName, owner: _repoOwner)
                .DiscussionCategories(first: 10)
                .Nodes
                .Select(x => new
                {
                    x.Id,
                    x.Name,
                })
                .Compile();

            var result = await connection.Run(query);
            return result.Single(x => x.Name == "Announcements").Id;
        }
        catch (Exception)
        {
            Logger.Log("[red]Error fetching repo ID[/]");
            throw;
        }
    }

    async Task<DiscussionSummary?> FindDiscussionUsingSearch(Connection connection, DisqusBlogPost post)
    {
        var hash = post.Sha1();
        try
        {
            var query = new Query()
                .Search($"{hash} repo:{_repo} in:body", SearchType.Discussion, first: 2)
                .Select(search => new
                {
                    search.DiscussionCount,
                    Discussion = search.Edges.Select(edge => new
                    {
                        Nodes = edge.Node.Select(node => node.Switch<DiscussionSummary>(
                            when => when.Discussion(discussion => new DiscussionSummary(
                                discussion.Title,
                                discussion.Body,
                                discussion.Id,
                                discussion.Number
                            )))).SingleOrDefault()
                    }).ToList()
                }).Compile();

            var result = await connection.Run(query);

            if (result.DiscussionCount == 0)
            {
                Logger.Log($"No discussion found for {post.GitHubDiscussionTitle} ({hash})");
                return null;
            }
            else if (result.DiscussionCount == 1)
            {
                var discussion = result.Discussion.ToList().Select(x => x.Nodes).Single();
                if (discussion.Title != post.GitHubDiscussionTitle
                    || !discussion.Body.Contains(hash))
                {
                    Logger.Log($"Found discussion {discussion.Number}, but discussion didn't match expected values:" + Environment.NewLine +
                                           $"Expected Title: {post.Title}, found: {discussion.Title}" + Environment.NewLine +
                                           $"Expected hash: {hash}, found body: {discussion.Body}");
                    throw new Exception("Error finding discussion ");
                }

                Logger.Log($"Found discussion {discussion.Number} for {post.Title}");
                return discussion;
            }
            else
            {
                Logger.Log($"Unexpectedly found {result.DiscussionCount} discussions for {post.Title}");
                throw new Exception("Error finding discussion");
            }
        }
        catch (Exception)
        {
            Logger.Log($"[red]Error searching for discussion with hash {hash}[/]");
            throw;
        }
    }

    DiscussionSummary? GetDiscussion(List<DiscussionSummary> discussions, DisqusBlogPost post)
    {
        var hash = post.Sha1();
        var discussion = discussions.SingleOrDefault(x => x.Body.Contains(hash));
        if (discussion is {})
        {
            Logger.Log($"Found discussion {discussion.Number} for {post.Title.EscapeMarkup()}");
            return discussion;
        }

        Logger.Log($"No discussion found for {post.GitHubDiscussionTitle.EscapeMarkup()} ({hash})");
        return null;
    }

    async Task<List<DiscussionSummary>> GetAllDiscussions(Connection connection, ID categoryId)
    {
        try
        {
            var results = new Dictionary<string, DiscussionSummary>();
            string? cursor = null;
            var orderBy = new DiscussionOrder() {Direction = OrderDirection.Asc, Field = DiscussionOrderField.CreatedAt};
            while (true)
            {
                var query = new Query()
                    .Repository(name: _repoName, owner: _repoOwner)
                    .Discussions(first: 100, after: cursor, categoryId: categoryId, orderBy: orderBy)
                    .Edges.Select(e => new
                    {
                        e.Cursor,
                        e.Node.Title,
                        e.Node.Body,
                        e.Node.Id,
                        e.Node.Number,
                    })
                    .Compile();

                var result = (await connection.Run(query)).ToList();
                if (!result.Any())
                {
                    return results.Values.ToList();
                }

                cursor = result.Last().Cursor;
                foreach (var discussion in result)
                {
                    results.TryAdd(discussion.Id.Value, new DiscussionSummary(
                        discussion.Title,
                        discussion.Body,
                        discussion.Id,
                        discussion.Number));
                }
            }
        }
        catch (Exception)
        {
            Logger.Log($"[red]Error fetching discussions [/]");
            throw;
        }
    }

    async Task<DiscussionSummary> CreateDiscussion(Connection connection, DisqusBlogPost post, ID repoId, ID categoryId)
    {
        try
        {
            var body = $"""
                        # {post.GitHubDiscussionTitle}

                        {post.MatchingPost?.Excerpt}

                        {post.Url}

                        <!-- sha1: {post.Sha1()} -->
                        """;

            var mutation = new Mutation()
                .CreateDiscussion(new CreateDiscussionInput
                {
                    Title = post.GitHubDiscussionTitle,
                    RepositoryId = repoId,
                    CategoryId = categoryId,
                    Body = body,
                })
                .Select(x => new
                {
                    x.Discussion.Title,
                    x.Discussion.Body,
                    x.Discussion.Id,
                    x.Discussion.Number,
                });

            var discussion = await connection.Run(mutation);
            if (discussion is not { })
            {
                throw new Exception($"Failed to create discussion for {post.GitHubDiscussionTitle}");
            }

            Logger.Log($"[cyan]Created discussion for post {post.Title}[/]");
            // adding a brief pause to avoid hitting abuse rate-limits
            // https://github.com/cli/cli/issues/4801
            await Task.Delay(3_000);
            return new DiscussionSummary(
                discussion.Title,
                discussion.Body,
                discussion.Id,
                discussion.Number);
        }
        catch (Exception)
        {
            Logger.Log($"[red]Error creating discussion for post {post.Title}[/]");
            throw;
        }
    }

    private async Task<DiscussionComment> CreateDiscussionComment(DiscussionSummary discussion, DisqusComment comment, DisqusComment? parentGithubComment)
    {
        // post comments _by_ me using my account, use bot account for others
        var connection = comment.Author.IsMe
            ? _mainConnection
            : _botConnection;

        var referencedParentComment = comment.ParentId is not null
            ? (parentGithubComment.Id == comment.ParentId
                ? parentGithubComment
                : parentGithubComment.ChildComments
                    .Single(c => c.Id == comment.ParentId))
            : null;

        var body = $"""
                   <em>{comment.Author.AuthorAndGitHubMarkdown} commented [on Disqus]({StaticData.DisqusForumLocation}) at {comment.CreatedAt.ToString("MMMM dd yyyy, hh:mm")}{GetParentCommentMarkdown(referencedParentComment)}</em>

                   ---

                   {comment.Message}
                   """;

        var mutation = new Mutation()
            .AddDiscussionComment(new AddDiscussionCommentInput
            {
                Body = body,
                DiscussionId = discussion.ID,
                ReplyToId = parentGithubComment?.GithubComment.Id,
            })
            .Select(x => new
            {
                x.Comment.Id,
                x.Comment.Url
            });
        
        var newComment = await connection.Run(mutation);
        if (newComment is not { })
        {
            throw new Exception($"Failed to create comment by {comment.Author.Username} for {discussion.Title}");
        }
        
        return new DiscussionComment(newComment.Id, newComment.Url);

        static string GetParentCommentMarkdown(DisqusComment? comment)
            => comment is null ? string.Empty : $", in reply to {comment.Author.AuthorMarkdown}'s [comment]({comment.GithubComment?.Url})";
    }
}