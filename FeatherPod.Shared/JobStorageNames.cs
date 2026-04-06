namespace FeatherPod.Shared;

/// <summary>
/// Single source of truth for normalization-job storage resource names.
/// Referenced by FeatherPod.Server (JobService), FeatherPod.Functions
/// (NormalizationFunction, CleanupFunction), and JobStatusEntity.
/// The literal values must match infrastructure/main.bicep (normalizationQueue,
/// normalizationTable) since Bicep cannot reference C# constants.
/// </summary>
public static class JobStorageNames
{
    /// <summary>Azure Table name that stores <see cref="Models.JobStatusEntity"/> rows.</summary>
    public const string TableName = "normalizationjobs";

    /// <summary>Azure Queue name that carries <see cref="Models.NormalizationJob"/> messages.</summary>
    public const string QueueName = "normalization-jobs";

    /// <summary>Partition key used for every job row in the table.</summary>
    public const string JobsPartitionKey = "jobs";
}
