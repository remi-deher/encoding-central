using Encoding.Launcher;
using Encoding.Launcher.Tests;

await LocalEnrollmentTests.RunAsync();
await AgentEnvironmentTests.RunAsync();
await SupervisorTests.RunAsync();
Console.WriteLine("PASS: all launcher scenarios.");
