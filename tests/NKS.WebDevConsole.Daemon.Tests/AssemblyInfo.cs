// Many test classes call SqliteConnection.ClearAllPools() to unlock their temp databases.
// That disposes pooled handles another class is still using when classes run in parallel.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
