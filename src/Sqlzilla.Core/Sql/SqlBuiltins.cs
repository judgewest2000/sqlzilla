namespace Sqlzilla.Sql;

public sealed record BuiltinFunction(string Name, IReadOnlyList<string> Parameters, string Returns, string Description)
{
    public string Signature => $"{Name}({string.Join(", ", Parameters)}) → {Returns}";
}

/// <summary>Signatures and one-line descriptions of common T-SQL built-in functions.</summary>
public static class SqlBuiltins
{
    // NAME(param; param; …) | returns | description   (';' separates parameters because some contain commas)
    private const string Catalog = """
        ABS(numeric_expression) | same as input | Absolute value.
        AVG([ALL | DISTINCT] expression) | numeric | Average of the values in a group, ignoring NULLs.
        CAST(expression AS data_type[(length)]) | data_type | Converts an expression to another data type.
        CEILING(numeric_expression) | same as input | Smallest integer greater than or equal to the value.
        CHARINDEX(expression_to_find; expression_to_search; [start_location]) | int | 1-based position of a substring, or 0 if not found.
        CHECKSUM(* | expression; ...) | int | Hash value computed over a row or expression list.
        CHOOSE(index; val_1; val_2; ...) | type of chosen value | Returns the item at the 1-based index.
        COALESCE(expression1; expression2; ...) | type of highest precedence | First non-NULL expression.
        CONCAT(string_value1; string_value2; ...) | string | Concatenates values; NULLs become empty strings.
        CONCAT_WS(separator; argument1; argument2; ...) | string | Concatenates values with a separator, skipping NULLs.
        CONVERT(data_type[(length)]; expression; [style]) | data_type | Converts an expression to another data type, with an optional style.
        COUNT([ALL | DISTINCT] expression | *) | int | Number of items in a group.
        COUNT_BIG([ALL | DISTINCT] expression | *) | bigint | Number of items in a group, as bigint.
        CUME_DIST() OVER (...) | float | Cumulative distribution of a value within a group.
        DATEADD(datepart; number; date) | type of date | Adds an interval to a date.
        DATEDIFF(datepart; startdate; enddate) | int | Number of datepart boundaries crossed between two dates.
        DATEDIFF_BIG(datepart; startdate; enddate) | bigint | DATEDIFF returning bigint.
        DATEFROMPARTS(year; month; day) | date | Builds a date from its parts.
        DATETIME2FROMPARTS(year; month; day; hour; minute; seconds; fractions; precision) | datetime2 | Builds a datetime2 from its parts.
        DATENAME(datepart; date) | nvarchar | Name of the datepart, e.g. 'January'.
        DATEPART(datepart; date) | int | Integer value of the datepart.
        DATETRUNC(datepart; date) | type of date | Truncates a date to the datepart.
        DAY(date) | int | Day of month.
        DB_ID([database_name]) | int | Database id.
        DB_NAME([database_id]) | nvarchar(128) | Database name.
        DENSE_RANK() OVER ([PARTITION BY ...] ORDER BY ...) | bigint | Rank without gaps.
        EOMONTH(start_date; [month_to_add]) | date | Last day of the month.
        ERROR_LINE() | int | Line number of the error that invoked CATCH.
        ERROR_MESSAGE() | nvarchar(4000) | Message of the error that invoked CATCH.
        ERROR_NUMBER() | int | Number of the error that invoked CATCH.
        ERROR_PROCEDURE() | nvarchar(128) | Procedure/trigger in which the error occurred.
        ERROR_SEVERITY() | int | Severity of the error that invoked CATCH.
        ERROR_STATE() | int | State of the error that invoked CATCH.
        EXP(float_expression) | float | e raised to the given power.
        FIRST_VALUE(scalar_expression) OVER (...) | type of expression | First value in an ordered window.
        FLOOR(numeric_expression) | same as input | Largest integer less than or equal to the value.
        FORMAT(value; format; [culture]) | nvarchar | Formats a value with a .NET format string.
        GETDATE() | datetime | Current local date and time.
        GETUTCDATE() | datetime | Current UTC date and time.
        GREATEST(expression1; expression2; ...) | type of highest precedence | Largest value in the list.
        HASHBYTES(algorithm; input) | varbinary(8000) | Hash (MD5, SHA2_256, SHA2_512…) of the input.
        IIF(boolean_expression; true_value; false_value) | type of highest precedence | Inline if.
        ISDATE(expression) | int | 1 if the expression is a valid date.
        ISJSON(expression; [json_type_constraint]) | bit | 1 if the string contains valid JSON.
        ISNULL(check_expression; replacement_value) | type of check_expression | Replaces NULL with the replacement value.
        ISNUMERIC(expression) | int | 1 if the expression is a valid numeric type.
        JSON_ARRAY(value; ...) | nvarchar(max) | Builds a JSON array.
        JSON_OBJECT(key:value; ...) | nvarchar(max) | Builds a JSON object.
        JSON_MODIFY(expression; path; newValue) | nvarchar(max) | Updates a value in a JSON string.
        JSON_QUERY(expression; [path]) | nvarchar(max) | Extracts an object or array from JSON.
        JSON_VALUE(expression; path) | nvarchar(4000) | Extracts a scalar value from JSON.
        LAG(scalar_expression; [offset]; [default]) OVER (...) | type of expression | Value from a previous row.
        LAST_VALUE(scalar_expression) OVER (...) | type of expression | Last value in an ordered window.
        LEAD(scalar_expression; [offset]; [default]) OVER (...) | type of expression | Value from a following row.
        LEAST(expression1; expression2; ...) | type of highest precedence | Smallest value in the list.
        LEFT(character_expression; integer_expression) | varchar/nvarchar | Leftmost characters.
        LEN(string_expression) | int | Number of characters, excluding trailing spaces.
        LOG(float_expression; [base]) | float | Natural (or given base) logarithm.
        LOWER(character_expression) | varchar/nvarchar | Lower case.
        LTRIM(character_expression; [characters]) | varchar/nvarchar | Removes leading spaces (or characters).
        MAX(expression) | type of expression | Maximum value in a group.
        MIN(expression) | type of expression | Minimum value in a group.
        MONTH(date) | int | Month number.
        NEWID() | uniqueidentifier | New random GUID.
        NEWSEQUENTIALID() | uniqueidentifier | Sequential GUID (column defaults only).
        NTILE(integer_expression) OVER (...) | bigint | Distributes rows into numbered groups.
        NULLIF(expression1; expression2) | type of expression1 | NULL if the expressions are equal, otherwise the first.
        OBJECT_ID('[db.][schema.]object_name'; ['object_type']) | int | Object id, or NULL.
        OBJECT_NAME(object_id; [database_id]) | sysname | Object name.
        OPENJSON(jsonExpression; [path]) [WITH (...)] | table | Parses JSON text into rows.
        PARSE(string_value AS data_type [USING culture]) | data_type | Parses a string with a culture.
        PATINDEX('%pattern%'; expression) | int | Position of a LIKE pattern.
        PERCENT_RANK() OVER (...) | float | Relative rank of a row.
        POWER(float_expression; y) | float | Value raised to a power.
        QUOTENAME(character_string; [quote_character]) | nvarchar(258) | Delimits a string as an identifier, e.g. [name].
        RAND([seed]) | float | Pseudo-random value between 0 and 1.
        RANK() OVER ([PARTITION BY ...] ORDER BY ...) | bigint | Rank with gaps.
        REPLACE(string_expression; string_pattern; string_replacement) | varchar/nvarchar | Replaces all occurrences of a substring.
        REPLICATE(string_expression; integer_expression) | varchar/nvarchar | Repeats a string.
        REVERSE(string_expression) | varchar/nvarchar | Reverses a string.
        RIGHT(character_expression; integer_expression) | varchar/nvarchar | Rightmost characters.
        ROUND(numeric_expression; length; [function]) | same as input | Rounds (or truncates when function ≠ 0) to length decimal places.
        ROW_NUMBER() OVER ([PARTITION BY ...] ORDER BY ...) | bigint | Sequential row number within a partition.
        RTRIM(character_expression; [characters]) | varchar/nvarchar | Removes trailing spaces (or characters).
        SCHEMA_NAME([schema_id]) | sysname | Schema name.
        SCOPE_IDENTITY() | numeric(38,0) | Last identity value inserted in the current scope.
        SERVERPROPERTY(propertyname) | sql_variant | Server property, e.g. 'ProductVersion'.
        SIGN(numeric_expression) | same as input | -1, 0 or 1.
        SPACE(integer_expression) | varchar | String of spaces.
        SQRT(float_expression) | float | Square root.
        STR(float_expression; [length]; [decimal]) | varchar | Number as text.
        STRING_AGG(expression; separator) [WITHIN GROUP (ORDER BY ...)] | varchar/nvarchar | Concatenates values in a group with a separator.
        STRING_ESCAPE(text; type) | nvarchar(max) | Escapes special characters (type 'json').
        STRING_SPLIT(string; separator; [enable_ordinal]) | table(value[, ordinal]) | Splits a string into rows.
        STUFF(character_expression; start; length; replace_with_expression) | varchar/nvarchar | Deletes and inserts characters at a position.
        SUBSTRING(expression; start; length) | varchar/nvarchar | Part of a string (1-based start).
        SUM([ALL | DISTINCT] expression) | numeric | Sum of the values in a group.
        SUSER_SNAME([server_user_sid]) | nvarchar(128) | Login name.
        SYSDATETIME() | datetime2(7) | Current local date and time, high precision.
        SYSDATETIMEOFFSET() | datetimeoffset(7) | Current date, time and offset.
        SYSUTCDATETIME() | datetime2(7) | Current UTC date and time, high precision.
        TRANSLATE(inputString; characters; translations) | varchar/nvarchar | Replaces characters one-for-one.
        TRIM([characters FROM] string) | varchar/nvarchar | Removes leading and trailing spaces (or characters).
        TRY_CAST(expression AS data_type[(length)]) | data_type | CAST that returns NULL on failure.
        TRY_CONVERT(data_type[(length)]; expression; [style]) | data_type | CONVERT that returns NULL on failure.
        TRY_PARSE(string_value AS data_type [USING culture]) | data_type | PARSE that returns NULL on failure.
        UPPER(character_expression) | varchar/nvarchar | Upper case.
        USER_NAME([id]) | nvarchar(128) | Database user name.
        YEAR(date) | int | Year number.
        """;

    public static IReadOnlyDictionary<string, BuiltinFunction> All { get; } = Parse();

    public static BuiltinFunction? Find(string name) => All.GetValueOrDefault(name);

    private static Dictionary<string, BuiltinFunction> Parse()
    {
        var all = new Dictionary<string, BuiltinFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Catalog.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Signatures may contain " | " themselves (e.g. "[ALL | DISTINCT]"), so split from the right.
            var descriptionAt = line.LastIndexOf(" | ", StringComparison.Ordinal);
            var returnsAt = line.LastIndexOf(" | ", descriptionAt - 1, StringComparison.Ordinal);
            var signature = line[..returnsAt];
            var open = signature.IndexOf('(');
            var close = open;
            for (var depth = 0; close < signature.Length; close++)
            {
                if (signature[close] == '(') depth++;
                else if (signature[close] == ')' && --depth == 0) break;
            }
            var name = signature[..open];
            var args = signature[(open + 1)..close];
            string[] parameters = args.Length == 0 ? [] : args.Split("; ");
            all[name] = new BuiltinFunction(name, parameters, line[(returnsAt + 3)..descriptionAt], line[(descriptionAt + 3)..]);
        }
        return all;
    }
}
