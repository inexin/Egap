namespace Egap.Spreadsheets.Xls;

/// <summary>
/// The built-in functions of BIFF8 formulas by their number ([MS-XLS] 2.5.198.17 Ftab and 2.5.198.18 Cetab), with the
/// argument count of those that PtgFunc calls with a fixed count; -1 for a function that takes a varying count.
/// </summary>
internal static class XlsFunctions
{
    private static readonly (string Name, int Arguments)[] Table = Build(
        "COUNT:-1 IF:-1 ISNA:1 ISERROR:1 SUM:-1 AVERAGE:-1 MIN:-1 MAX:-1 ROW:-1 COLUMN:-1 NA:0 NPV:-1 STDEV:-1 DOLLAR:-1 FIXED:-1 "
        + "SIN:1 COS:1 TAN:1 ATAN:1 PI:0 SQRT:1 EXP:1 LN:1 LOG10:1 ABS:1 INT:1 SIGN:1 ROUND:2 LOOKUP:-1 INDEX:-1 REPT:2 MID:3 "
        + "LEN:1 VALUE:1 TRUE:0 FALSE:0 AND:-1 OR:-1 NOT:1 MOD:2 DCOUNT:3 DSUM:3 DAVERAGE:3 DMIN:3 DMAX:3 DSTDEV:3 VAR:-1 DVAR:3 "
        + "TEXT:2 LINEST:-1 TREND:-1 LOGEST:-1 GROWTH:-1 GOTO:-1 HALT:-1 RETURN:-1 PV:-1 FV:-1 NPER:-1 PMT:-1 RATE:-1 MIRR:3 "
        + "IRR:-1 RAND:0 MATCH:-1 DATE:3 TIME:3 DAY:1 MONTH:1 YEAR:1 WEEKDAY:-1 HOUR:1 MINUTE:1 SECOND:1 NOW:0 AREAS:1 ROWS:1 "
        + "COLUMNS:1 OFFSET:-1 ABSREF:2 RELREF:2 ARGUMENT:-1 SEARCH:-1 TRANSPOSE:1 ERROR:-1 STEP:0 TYPE:1 ECHO:-1 SET.NAME:-1 "
        + "CALLER:0 DEREF:1 WINDOWS:-1 SERIES:-1 DOCUMENTS:-1 ACTIVE.CELL:0 SELECTION:0 RESULT:-1 ATAN2:2 ASIN:1 ACOS:1 "
        + "CHOOSE:-1 HLOOKUP:-1 VLOOKUP:-1 LINKS:-1 INPUT:-1 ISREF:1 GET.FORMULA:1 GET.NAME:-1 SET.VALUE:2 LOG:-1 EXEC:-1 "
        + "CHAR:1 LOWER:1 UPPER:1 PROPER:1 LEFT:-1 RIGHT:-1 EXACT:2 TRIM:1 REPLACE:4 SUBSTITUTE:-1 CODE:1 NAMES:-1 "
        + "DIRECTORY:-1 FIND:-1 CELL:-1 ISERR:1 ISTEXT:1 ISNUMBER:1 ISBLANK:1 T:1 N:1 FOPEN:-1 FCLOSE:1 FSIZE:1 FREADLN:1 "
        + "FREAD:2 FWRITELN:2 FWRITE:2 FPOS:-1 DATEVALUE:1 TIMEVALUE:1 SLN:3 SYD:4 DDB:-1 GET.DEF:-1 REFTEXT:-1 TEXTREF:-1 "
        + "INDIRECT:-1 REGISTER:-1 CALL:-1 ADD.BAR:-1 ADD.MENU:-1 ADD.COMMAND:-1 ENABLE.COMMAND:-1 CHECK.COMMAND:-1 "
        + "RENAME.COMMAND:-1 SHOW.BAR:-1 DELETE.MENU:-1 DELETE.COMMAND:-1 GET.CHART.ITEM:-1 DIALOG.BOX:1 CLEAN:1 MDETERM:1 "
        + "MINVERSE:1 MMULT:2 FILES:-1 IPMT:-1 PPMT:-1 COUNTA:-1 CANCEL.KEY:-1 FOR:-1 WHILE:1 BREAK:0 NEXT:0 INITIATE:2 "
        + "REQUEST:2 POKE:3 EXECUTE:2 TERMINATE:1 RESTART:-1 HELP:-1 GET.BAR:-1 PRODUCT:-1 FACT:1 GET.CELL:-1 "
        + "GET.WORKSPACE:1 GET.WINDOW:-1 GET.DOCUMENT:-1 DPRODUCT:3 ISNONTEXT:1 GET.NOTE:-1 NOTE:-1 STDEVP:-1 VARP:-1 "
        + "DSTDEVP:3 DVARP:3 TRUNC:-1 ISLOGICAL:1 DCOUNTA:3 DELETE.BAR:1 UNREGISTER:1 : : USDOLLAR:-1 FINDB:-1 SEARCHB:-1 "
        + "REPLACEB:4 LEFTB:-1 RIGHTB:-1 MIDB:3 LENB:1 ROUNDUP:2 ROUNDDOWN:2 ASC:1 DBCS:1 RANK:-1 : : ADDRESS:-1 DAYS360:-1 "
        + "TODAY:0 VDB:-1 ELSE:0 ELSE.IF:1 END.IF:0 FOR.CELL:-1 MEDIAN:-1 SUMPRODUCT:-1 SINH:1 COSH:1 TANH:1 ASINH:1 ACOSH:1 "
        + "ATANH:1 DGET:3 CREATE.OBJECT:-1 VOLATILE:-1 LAST.ERROR:0 CUSTOM.UNDO:-1 CUSTOM.REPEAT:-1 FORMULA.CONVERT:-1 "
        + "GET.LINK.INFO:-1 TEXT.BOX:-1 INFO:1 GROUP:0 GET.OBJECT:-1 DB:-1 PAUSE:-1 : : RESUME:-1 FREQUENCY:2 "
        + "ADD.TOOLBAR:-1 DELETE.TOOLBAR:1 : RESET.TOOLBAR:1 EVALUATE:1 GET.TOOLBAR:-1 GET.TOOL:-1 SPELLING.CHECK:-1 "
        + "ERROR.TYPE:1 APP.TITLE:-1 WINDOW.TITLE:-1 SAVE.TOOLBAR:-1 ENABLE.TOOL:3 PRESS.TOOL:3 REGISTER.ID:-1 "
        + "GET.WORKBOOK:-1 AVEDEV:-1 BETADIST:-1 GAMMALN:1 BETAINV:-1 BINOMDIST:4 CHIDIST:2 CHIINV:2 COMBIN:2 CONFIDENCE:3 "
        + "CRITBINOM:3 EVEN:1 EXPONDIST:3 FDIST:3 FINV:3 FISHER:1 FISHERINV:1 FLOOR:2 GAMMADIST:4 GAMMAINV:3 CEILING:2 "
        + "HYPGEOMDIST:4 LOGNORMDIST:3 LOGINV:3 NEGBINOMDIST:3 NORMDIST:4 NORMSDIST:1 NORMINV:3 NORMSINV:1 STANDARDIZE:3 "
        + "ODD:1 PERMUT:2 POISSON:3 TDIST:3 WEIBULL:4 SUMXMY2:2 SUMX2MY2:2 SUMX2PY2:2 CHITEST:2 CORREL:2 COVAR:2 FORECAST:3 "
        + "FTEST:2 INTERCEPT:2 PEARSON:2 RSQ:2 STEYX:2 SLOPE:2 TTEST:4 PROB:-1 DEVSQ:-1 GEOMEAN:-1 HARMEAN:-1 SUMSQ:-1 "
        + "KURT:-1 SKEW:-1 ZTEST:-1 LARGE:2 SMALL:2 QUARTILE:2 PERCENTILE:2 PERCENTRANK:-1 MODE:-1 TRIMMEAN:2 TINV:2 : "
        + "MOVIE.COMMAND:-1 GET.MOVIE:-1 CONCATENATE:-1 POWER:2 PIVOT.ADD.DATA:-1 GET.PIVOT.TABLE:-1 GET.PIVOT.FIELD:-1 "
        + "GET.PIVOT.ITEM:-1 RADIANS:1 DEGREES:1 SUBTOTAL:-1 SUMIF:-1 COUNTIF:2 COUNTBLANK:1 SCENARIO.GET:-1 "
        + "OPTIONS.LISTS.GET:1 ISPMT:4 DATEDIF:3 DATESTRING:1 NUMBERSTRING:2 ROMAN:-1 OPEN.DIALOG:-1 SAVE.DIALOG:-1 "
        + "VIEW.GET:-1 GETPIVOTDATA:-1 HYPERLINK:-1 PHONETIC:1 AVERAGEA:-1 MAXA:-1 MINA:-1 STDEVPA:-1 VARPA:-1 STDEVA:-1 "
        + "VARA:-1 BAHTTEXT:1 THAIDAYOFWEEK:1 THAIDIGIT:1 THAIMONTHOFYEAR:1 THAINUMSOUND:1 THAINUMSTRING:1 "
        + "THAISTRINGLENGTH:1 ISTHAIDIGIT:1 ROUNDBAHTDOWN:1 ROUNDBAHTUP:1 THAIYEAR:1 RTD:-1");

    /// <summary>
    /// The numbers past BIFF8's table that BIFF12 formulas use for Excel 2007's functions and the Analysis ToolPak's
    /// ([MS-XLSB] 2.5.97.10 Ftab), from 380 on.
    /// </summary>
    private static readonly (string Name, int Arguments)[] Biff12Table = Build(
        "CUBEVALUE:-1 CUBEMEMBER:-1 CUBEMEMBERPROPERTY:3 CUBERANKEDMEMBER:-1 HEX2BIN:-1 HEX2DEC:1 HEX2OCT:-1 DEC2BIN:-1 "
        + "DEC2HEX:-1 DEC2OCT:-1 OCT2BIN:-1 OCT2HEX:-1 OCT2DEC:1 BIN2DEC:1 BIN2OCT:-1 BIN2HEX:-1 IMSUB:2 IMDIV:2 IMPOWER:2 "
        + "IMABS:1 IMSQRT:1 IMLN:1 IMLOG2:1 IMLOG10:1 IMSIN:1 IMCOS:1 IMEXP:1 IMARGUMENT:1 IMCONJUGATE:1 IMAGINARY:1 IMREAL:1 "
        + "COMPLEX:-1 IMSUM:-1 IMPRODUCT:-1 SERIESSUM:4 FACTDOUBLE:1 SQRTPI:1 QUOTIENT:2 DELTA:-1 GESTEP:-1 ISEVEN:1 ISODD:1 "
        + "MROUND:2 ERF:-1 ERFC:1 BESSELJ:2 BESSELK:2 BESSELY:2 BESSELI:2 XIRR:-1 XNPV:3 PRICEMAT:-1 YIELDMAT:-1 INTRATE:-1 "
        + "RECEIVED:-1 DISC:-1 PRICEDISC:-1 YIELDDISC:-1 TBILLEQ:3 TBILLPRICE:3 TBILLYIELD:3 PRICE:-1 YIELD:-1 DOLLARDE:2 "
        + "DOLLARFR:2 NOMINAL:2 EFFECT:2 CUMPRINC:6 CUMIPMT:6 EDATE:2 EOMONTH:2 YEARFRAC:-1 COUPDAYBS:-1 COUPDAYS:-1 "
        + "COUPDAYSNC:-1 COUPNCD:-1 COUPNUM:-1 COUPPCD:-1 DURATION:-1 MDURATION:-1 ODDLPRICE:-1 ODDLYIELD:-1 ODDFPRICE:-1 "
        + "ODDFYIELD:-1 RANDBETWEEN:2 WEEKNUM:-1 AMORDEGRC:-1 AMORLINC:-1 CONVERT:3 ACCRINT:-1 ACCRINTM:-1 WORKDAY:-1 "
        + "NETWORKDAYS:-1 GCD:-1 MULTINOMIAL:-1 LCM:-1 FVSCHEDULE:2 CUBEKPIMEMBER:-1 CUBESET:-1 CUBESETCOUNT:1 IFERROR:2 "
        + "COUNTIFS:-1 SUMIFS:-1 AVERAGEIF:-1 AVERAGEIFS:-1");

    /// <summary>Excel 2007's functions, which an XLS file calls through a name with the <c>_xlfn.</c> prefix and an XLSX file by name alone.</summary>
    private static readonly HashSet<string> Excel2007 = new(StringComparer.OrdinalIgnoreCase)
    {
        "AVERAGEIF", "AVERAGEIFS", "COUNTIFS", "CUBEKPIMEMBER", "CUBEMEMBER", "CUBEMEMBERPROPERTY", "CUBERANKEDMEMBER",
        "CUBESET", "CUBESETCOUNT", "CUBEVALUE", "IFERROR", "SUMIFS",
    };

    /// <summary>The name and fixed argument count of a function number; null for a number the table lacks.</summary>
    public static (string Name, int Arguments)? Function(int number) =>
        (uint)number < (uint)Table.Length && Table[number].Name.Length > 0 ? Table[number] : null;

    /// <summary>The name and fixed argument count of a BIFF12 function number, which goes on past BIFF8's; null for one neither table has.</summary>
    public static (string Name, int Arguments)? Biff12Function(int number) =>
        number >= Table.Length && number - Table.Length < Biff12Table.Length ? Biff12Table[number - Table.Length] : Function(number);

    /// <summary>A name a file calls as a function, as an XLSX file writes it: Excel 2007's functions lose <c>_xlfn.</c>.</summary>
    public static string CallName(string name) =>
        name.StartsWith("_xlfn.", StringComparison.OrdinalIgnoreCase) && Excel2007.Contains(name[6..]) ? name[6..] : name;

    private static (string, int)[] Build(string list)
    {
        string[] items = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var table = new (string, int)[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            int colon = items[i].LastIndexOf(':');
            table[i] = colon <= 0 ? (string.Empty, -1) : (items[i][..colon], int.Parse(items[i][(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        }

        return table;
    }
}
