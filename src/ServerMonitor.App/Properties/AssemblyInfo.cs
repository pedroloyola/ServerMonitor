using System.Runtime.CompilerServices;

// Grants the test project access to internal QA harness types (mirrors the Infrastructure pattern).
[assembly: InternalsVisibleTo("ServerMonitor.App.Tests")]
