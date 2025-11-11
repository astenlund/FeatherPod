namespace FeatherPod.Tests;

/// <summary>
/// Defines a collection that runs tests sequentially to avoid file locking issues
/// </summary>
[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialCollection
{
}
