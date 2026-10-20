using Microsoft.VisualStudio.TestTools.UnitTesting;

// WPF Application and the legacy regression counters have process-wide state.
// Concurrency tests explicitly start their own simultaneous clients inside individual test cases.
[assembly: DoNotParallelize]
