using Newtonsoft.Json;

internal class Checkpointer
{
    private readonly string _checkpointPath;

    public Checkpointer(string checkpointPath)
    {
        _checkpointPath = checkpointPath;
    }

    public void Checkpoint(Status status, List<DisqusBlogPost> blogPosts)
    {
        using var file = new FileStream(_checkpointPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file);
        using var jsonWriter = new JsonTextWriter(writer);
        var serializer = new JsonSerializer();

        serializer.Serialize(jsonWriter, new CheckpointResults {Status = status, DisqusTree = blogPosts});
    }

    public CheckpointResults TryLoad()
    {
        try
        {
            using var file = File.OpenRead(_checkpointPath);
            using var sr = new StreamReader(file);
            using var reader = new JsonTextReader(sr);
            var serializer = new JsonSerializer();

            return serializer.Deserialize<CheckpointResults>(reader)!;
        }
        catch (Exception)
        {
            return new CheckpointResults
            {
                Status = Status.Unparsed,
                DisqusTree = new List<DisqusBlogPost>(),
            };
        }
        
    }

    internal record CheckpointResults
    {
        public Status Status { get; init ; }
        public List<DisqusBlogPost> DisqusTree { get; init; }

        public void Deconstruct(out Status status, out List<DisqusBlogPost> disqusTree)
        {
            status = Status;
            disqusTree = DisqusTree;
        }
    }
}