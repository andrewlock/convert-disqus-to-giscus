using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Octokit.GraphQL;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<ConvertDisqusToGiscusCommand>();
return app.Run(args);

internal sealed class ConvertDisqusToGiscusCommand : AsyncCommand<ConvertDisqusToGiscusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("The XML source file.")]
        [CommandArgument(0, "<source>")]
        public required string Source { get; init; }

        [Description("A GitHub PersonalAccessToken for the 'main' user i.e. your account")]
        [CommandOption("-t|--token")]
        public required string MainToken { get; init; }

        [Description("A GitHub PersonalAccessToken for the 'bot' user for creating comments")]
        [CommandOption("-b|--bot-token")]
        public string? BotToken { get; init; }

        [Description("The checkpoint file")]
        [CommandOption("-c|--checkpoint")]
        public required string CheckpointFile { get; init; }
    }

    public override ValidationResult Validate(CommandContext context, Settings settings)
    {
        if (!File.Exists(settings.Source))
        {
            return ValidationResult.Error($"Could not find specified file '{settings.Source}'");
        }

        if (string.IsNullOrEmpty(settings.MainToken))
        {
            return ValidationResult.Error($"A token is required");
        }

        if (string.IsNullOrEmpty(settings.CheckpointFile))
        {
            return ValidationResult.Error($"A checkpoint file path is required");
        }

        return base.Validate(context, settings);
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var mainToken = settings.MainToken;
        var botToken = settings.BotToken ?? settings.MainToken;
        var xmlPath = settings.Source;
        var checkpointFile = settings.CheckpointFile;

        var checkpointer = new Checkpointer(checkpointFile);
        var (status, results) = checkpointer.TryLoad();
        if (status == Status.Unparsed)
        {
            var validUrls = BlogPostReader.GetValidPosts();
            results = XmlParser.Parse(xmlPath, validUrls, StaticData.ForcedComments, StaticData.UserMapping, StaticData.MyDisqusAccount);
            status = Status.ParsingComplete;
            checkpointer.Checkpoint(status, results);
        }

        var github = new GitHubHelper(StaticData.MyGitHubAccount, StaticData.BlogCommentsRepo, mainToken, botToken);
        if (status == Status.ParsingComplete)
        {
            await github.AssociateDiscussions(results);
            status = Status.DiscussionsAssociated;
            checkpointer.Checkpoint(status, results);
        }

        if (status == Status.DiscussionsAssociated)
        {
            await github.AddComments(results, () => checkpointer.Checkpoint(status, results));
            status = Status.CommentsAssociated;
            checkpointer.Checkpoint(status, results);
        }

        return 0;
    }
}

record DisqusBlogPost(long Id, string Title, string Url, DateTime CreatedAt)
{
    public List<DisqusComment> Comments { get; } = new();

    public BlogPost? MatchingPost { get; set; }

    public DiscussionSummary? GithubDiscussion { get; set; }

    // Strip the leading `/` off
    public string GitHubDiscussionTitle { get; } = new Uri(Url).AbsolutePath.Substring(1);

    public string Sha1()
    {
        var bytes = Encoding.ASCII.GetBytes(GitHubDiscussionTitle);
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

record DisqusComment(long Id, long PostId, long? ParentId, DateTime CreatedAt, Author Author)
{
    public List<DisqusComment> ChildComments { get; } = new();

    public DiscussionComment? GithubComment { get; set; }

    public string Message { get; set; }
}

record Author(string Name, string? Username, string? GitHubUser, bool IsAnonymous, bool IsMe)
{
    public string AuthorMarkdown => $"[{Name}](https://disqus.com/by/{Username}/)";
    public string AuthorAnchor => $"<a href=\"https://disqus.com/by/{Username}/\">{Name}</a>";

    public string AuthorAndGitHubMarkdown => $"[{Name}](https://disqus.com/by/{Username}/)"
                                             + (GitHubUser is null ? "" : $" (@{GitHubUser})");
}

record DiscussionSummary(string Title, string Body, ID ID, int Number);

record DiscussionComment(ID Id, string Url);

class BlogPost
{
    public string Title { get; init; }
    public string Excerpt { get; init; }
    public string FilePath { get; set; }
    public string Url { get; set; }
};

enum Status
{
    Unparsed = 0,
    ParsingComplete = 1,
    DiscussionsAssociated = 2,
    CommentsAssociated = 3,
}

public static class StaticData
{
    public const string MyDisqusAccount = "andrewlockdotnet";
    public const string MyGitHubAccount = "andrewlock";
    public const string BlogCommentsRepo = "blog-comments";
    public const string DisqusForumLocation = "https://disqus.com/home/forum/andrewlock/";

    public static Dictionary<long, bool> ForcedComments { get; } = new()
    {
        {2904807172, true}, // message marked as spam that shouldn't be
        {3333474667, true}, // deleted message with reply
        {3492224079, true}, // message marked as spam that shouldn't be
        {3376838338, false}, // reply to deleted message
        {3558257830, false}, // reply to deleted message
        {3571650938, false}, // reply to deleted message
        {3643680471, false}, // reply to deleted message
        {3643810407, false}, // reply to deleted message
        {4341940773, false}, // reply to deleted message
        {4342882166, false}, // reply to deleted message
        {4380614085, false}, // reply to deleted message
        {4381353329, false}, // reply to deleted message
        {4417016416, false}, // reply to deleted message
        {4575391996, false}, // reply to deleted message
        {4680699810, false}, // reply to deleted message
        {4710366421, false}, // reply to deleted message
    };

    /// <summary>
    /// Disqus username key, github username value
    /// </summary>
    public static Dictionary<string, string> UserMapping { get; } = new()
    {
    };
}