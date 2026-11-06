namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Arguments near the top of a double's range, which Excel doesn't accept as literals or takes minutes over: found by fuzzing,
/// each used to run for hours or to throw. Any answer will do, but it must come soon.
/// </summary>
public class FunctionHugeArgumentTests
{
    [Theory]
    [InlineData("PERMUT(1E+308,0.5)")]
    [InlineData("COMBIN(2147483646,1000000000)")]
    [InlineData("DB(1,1E-300,1E+15,1E+15)")]
    [InlineData("DB(3,1000000,4294967296,4294967296)")]
    [InlineData("DB(5000,44927,1E+308,1E+308)")]
    [InlineData("CUMIPMT(1,4294967296,2,2,4294967296,1)")]
    [InlineData("CUMPRINC(1,1E+308,1,1,4294967296,1)")]
    [InlineData("BINOM.DIST.RANGE(1E+15,0.5,2,1E+15)")]
    [InlineData("BINOM.DIST.RANGE(2147483646,0.5,2,2000000000)")]
    [InlineData("VDB(1E+308,1,1E+15,1,1E+15)")]
    [InlineData("VDB(3,0,2147483648,1,2147483648,2,TRUE)")]
    [InlineData("AMORLINC(1E+15,36526,36526,100,1E+15,1E-15,1)")]
    [InlineData("AMORDEGRC(1E+15,36526,36526,100,1E+8,1E-9,1)")]
    [InlineData("AMORLINC(1000,2147483648,2147483648,100,1,0.1,1)")]
    [InlineData("AMORDEGRC(1000,1,4294967296,100,1,0.1,1)")]
    [InlineData("ACCRINT(36526,36892,2958465,0.1,1000,1,0)")]
    [InlineData("ACCRINT(0.5,5000,4294967296,0.1,1000,1,0)")]
    [InlineData("BETA.INV(0.5,1E+308,3)")]
    [InlineData("BETA.INV(0.5,1E+308,1E-308)")]
    [InlineData("GAMMA.INV(0.5,1E+308,1)")]
    [InlineData("CONFIDENCE.T(1E-308,0.5,1E+308)")]
    [InlineData("POISSON(36526,1E+308,TRUE)")]
    [InlineData("CHISQ.DIST(1E+308,2,TRUE)")]
    [InlineData("CHISQ.DIST.RT(1E+308,2)")]
    public async Task AnswersSoon(string formula)
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        await Task.Run(() => sheet.Evaluate(formula)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}
