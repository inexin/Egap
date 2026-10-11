# Egap.Pdf.Fuzz

Fuzzing harness for Egap's readers, built on [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and AFL++.

Each target reads one input and must either succeed or fail with Egap's own exception type. Any other exception is a crash.
The scaffold has only the `noop` target; real targets arrive with the PDF parser.

```
dotnet run --project tests/Egap.Pdf.Fuzz -- noop file.pdf
```

To run under AFL++ (Linux), instrument `Egap.Pdf.dll` with `sharpfuzz` and start `afl-fuzz` with the harness reading from
stdin. Keep local runs short (about 10 minutes) on shared machines.
