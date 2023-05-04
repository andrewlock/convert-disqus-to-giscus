using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

internal class BlogPostReader
{
    public static List<BlogPost> GetValidPosts()
    {
        var markdownPipeline = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .UsePipeTables()
            .UseAutoLinks()
            .Build();

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var posts = Directory.EnumerateFiles(@"C:\repos\metalsmith-blog\posts")
            .Concat(Directory.EnumerateFiles(@"C:\repos\metalsmith-blog\pending"))
            .Select(ReadMetadata)
            .Select(x =>
            {
                x.Url = "https://andrewlock.net/" + Path.GetFileNameWithoutExtension(x.FilePath) + "/";
                return x;
            });

        var series = Directory.EnumerateFiles(@"C:\repos\metalsmith-blog\series")
            .Select(ReadMetadata)
            .Select(x =>
            {
                x.Url = "https://andrewlock.net/series/" + Path.GetFileNameWithoutExtension(x.FilePath) + "/";
                return x;
            });

        var allValid = posts.Concat(series).ToList();
        AnsiConsole.WriteLine($"Found {allValid.Count} valid posts");
        return allValid;

        BlogPost ReadMetadata(string filename)
        {
            var fileContents = File.ReadAllText(filename);
            var markdownDoc = Markdown.Parse(fileContents, markdownPipeline);
            var frontMatter = markdownDoc.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
            if (frontMatter is null)
            {
                return null;
            }

            const string separator = "---";
            var frontMatterSpan = fileContents.AsSpan().Slice(frontMatter.Span.Start, frontMatter.Span.Length);
            var from = frontMatterSpan.IndexOf(separator);
            var to = frontMatterSpan.LastIndexOf(separator);
            var slice = frontMatterSpan.Slice(from + 3, to - from - 3).Trim();
            var post = deserializer.Deserialize<BlogPost>(slice.ToString())!;
            post.FilePath = filename;
            if (string.IsNullOrEmpty(post.Excerpt))
            {
                throw new Exception($"Excerpt in {filename} was missing");
            }

            if (string.IsNullOrEmpty(post.Title))
            {
                throw new Exception($"Title in {filename} was missing");
            }

            return post;
        }
    }
}