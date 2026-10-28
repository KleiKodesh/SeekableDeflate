// SeekableDeflate harness:  dotnet run -c Release [-- --megabytes 32]
using SeekableDeflate.Tests;

int megabytes = 8;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--megabytes") megabytes = int.Parse(args[++i]);

DeflateInflaterRoundTripTest.Run(megabytes);

Console.WriteLine();
Console.WriteLine(TestReport.FailureCount == 0 ? "ALL PASSED" : $"{TestReport.FailureCount} FAILURE(S)");
return TestReport.FailureCount == 0 ? 0 : 1;
