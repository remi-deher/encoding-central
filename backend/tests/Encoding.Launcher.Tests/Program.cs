using Encoding.Core.Nodes;
using Encoding.Launcher;
using Encoding.Launcher.Tests;

await NodeModeTests.RunAsync();
await LocalEnrollmentTests.RunAsync();
await SupervisorTests.RunAsync();
await EndpointTests.RunAsync();
Console.WriteLine("PASS: all launcher scenarios.");
