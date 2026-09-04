using System.Globalization;
using System.Text;

namespace StorageHub.Desktop;

internal sealed class VtTerminalBuffer
{
    private static readonly Color DefaultForeground = Color.FromArgb(226, 232, 240);
    private static readonly Color DefaultBackground = Color.FromArgb(12, 18, 28);
    private static readonly Color[] AnsiColors =
    [
        Color.FromArgb(15, 23, 42), Color.FromArgb(220, 38, 38),
        Color.FromArgb(22, 163, 74), Color.FromArgb(202, 138, 4),
        Color.FromArgb(37, 99, 235), Color.FromArgb(147, 51, 234),
        Color.FromArgb(8, 145, 178), Color.FromArgb(203, 213, 225),
        Color.FromArgb(100, 116, 139), Color.FromArgb(248, 113, 113),
        Color.FromArgb(74, 222, 128), Color.FromArgb(250, 204, 21),
        Color.FromArgb(96, 165, 250), Color.FromArgb(216, 180, 254),
        Color.FromArgb(34, 211, 238), Color.FromArgb(248, 250, 252)
    ];

    private readonly List<TerminalCell[]> _history = [];
    private readonly StringBuilder _sequence = new();
    private readonly int _maximumScrollbackLines;
    private TerminalCell[][] _screen;
    private ParserState _state;
    private int _row;
    private int _column;
    private int _savedRow;
    private int _savedColumn;
    private Color _foreground = DefaultForeground;
    private Color _background = DefaultBackground;
    private bool _bold;
    private int _scrollTop;
    private int _scrollBottom;

    internal VtTerminalBuffer(int columns, int rows, int maximumScrollbackLines = 2_000)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        _maximumScrollbackLines = Math.Clamp(maximumScrollbackLines, 100, 20_000);
        _scrollBottom = Rows - 1;
        _screen = Enumerable.Range(0, Rows).Select(_ => BlankLine()).ToArray();
    }

    internal int Columns { get; private set; }
    internal int Rows { get; private set; }

    internal void Feed(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            Process(character);
        }
    }

    internal void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        var resized = Enumerable.Range(0, rows)
            .Select(_ => Enumerable.Repeat(BlankCell(), columns).ToArray())
            .ToArray();
        for (var row = 0; row < Math.Min(rows, Rows); row++)
        {
            Array.Copy(_screen[row], resized[row], Math.Min(columns, Columns));
        }
        Columns = columns;
        Rows = rows;
        _screen = resized;
        _row = Math.Clamp(_row, 0, Rows - 1);
        _column = Math.Clamp(_column, 0, Columns - 1);
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    internal VtTerminalSnapshot Snapshot()
    {
        var lines = _history.Concat(_screen).ToArray();
        var text = new StringBuilder(lines.Length * (Columns + 1));
        var runs = new List<VtStyleRun>();
        TerminalCell? previous = null;
        var runStart = 0;
        foreach (var line in lines)
        {
            foreach (var cell in line)
            {
                if (previous is { } style && !style.SameStyle(cell))
                {
                    runs.Add(new VtStyleRun(runStart, text.Length - runStart, style.Foreground, style.Background, style.Bold));
                    runStart = text.Length;
                }
                previous = cell;
                text.Append(cell.Character);
            }
            text.Append('\n');
        }
        if (previous is { } final && text.Length > runStart)
        {
            runs.Add(new VtStyleRun(runStart, text.Length - runStart, final.Foreground, final.Background, final.Bold));
        }

        var cursor = _history.Count * (Columns + 1) + _row * (Columns + 1) + _column;
        return new VtTerminalSnapshot(text.ToString(), runs, Math.Min(cursor, text.Length));
    }

    private void Process(char character)
    {
        switch (_state)
        {
            case ParserState.Escape:
                ProcessEscape(character);
                return;
            case ParserState.Csi:
                if (character is >= '@' and <= '~')
                {
                    ExecuteCsi(character, _sequence.ToString());
                    _sequence.Clear();
                    _state = ParserState.Normal;
                }
                else if (_sequence.Length < 128)
                {
                    _sequence.Append(character);
                }
                return;
            case ParserState.Osc:
                if (character == '\a')
                {
                    _state = ParserState.Normal;
                }
                else if (character == '\u001b')
                {
                    _state = ParserState.OscEscape;
                }
                return;
            case ParserState.OscEscape:
                _state = character == '\\' ? ParserState.Normal : ParserState.Osc;
                return;
        }

        switch (character)
        {
            case '\u001b':
                _state = ParserState.Escape;
                break;
            case '\r':
                _column = 0;
                break;
            case '\n':
                NewLine();
                break;
            case '\b':
                _column = Math.Max(0, _column - 1);
                break;
            case '\t':
                _column = Math.Min(Columns - 1, ((_column / 8) + 1) * 8);
                break;
            case '\a':
                break;
            default:
                if (!char.IsControl(character))
                {
                    Put(character);
                }
                break;
        }
    }

    private void ProcessEscape(char character)
    {
        _state = ParserState.Normal;
        switch (character)
        {
            case '[':
                _sequence.Clear();
                _state = ParserState.Csi;
                break;
            case ']':
                _state = ParserState.Osc;
                break;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'c':
                Reset();
                break;
        }
    }

    private void ExecuteCsi(char command, string sequence)
    {
        var parameters = ParseParameters(sequence);
        var first = parameters.Length == 0 ? 0 : parameters[0];
        var amount = Math.Max(1, first);
        switch (command)
        {
            case 'A': _row = Math.Max(0, _row - amount); break;
            case 'B': _row = Math.Min(Rows - 1, _row + amount); break;
            case 'C': _column = Math.Min(Columns - 1, _column + amount); break;
            case 'D': _column = Math.Max(0, _column - amount); break;
            case 'E': _row = Math.Min(Rows - 1, _row + amount); _column = 0; break;
            case 'F': _row = Math.Max(0, _row - amount); _column = 0; break;
            case 'G': _column = Math.Clamp(amount - 1, 0, Columns - 1); break;
            case 'd': _row = Math.Clamp(amount - 1, 0, Rows - 1); break;
            case 'H':
            case 'f':
                _row = Math.Clamp((parameters.ElementAtOrDefault(0) is 0 ? 1 : parameters[0]) - 1, 0, Rows - 1);
                _column = Math.Clamp((parameters.ElementAtOrDefault(1) is 0 ? 1 : parameters[1]) - 1, 0, Columns - 1);
                break;
            case 'J': EraseDisplay(first); break;
            case 'K': EraseLine(first); break;
            case 'X': EraseCharacters(amount); break;
            case '@': InsertCharacters(amount); break;
            case 'P': DeleteCharacters(amount); break;
            case 'L': InsertLines(amount); break;
            case 'M': DeleteLines(amount); break;
            case 'S': ScrollUp(amount); break;
            case 'T': ScrollDown(amount); break;
            case 'm': ApplyGraphics(parameters); break;
            case 'r': SetScrollRegion(parameters); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
        }
    }

    private void Put(char character)
    {
        _screen[_row][_column] = new TerminalCell(character, _foreground, _background, _bold);
        _column++;
        if (_column >= Columns)
        {
            _column = 0;
            NewLine();
        }
    }

    private void NewLine()
    {
        if (_row < _scrollBottom)
        {
            _row++;
            return;
        }
        if (_row != _scrollBottom)
        {
            _row = Math.Min(Rows - 1, _row + 1);
            return;
        }
        ScrollUp(1);
    }

    private void ScrollUp(int amount)
    {
        amount = Math.Clamp(amount, 1, _scrollBottom - _scrollTop + 1);
        for (var count = 0; count < amount; count++)
        {
            if (_scrollTop == 0 && _scrollBottom == Rows - 1)
            {
                _history.Add(_screen[0]);
            }
            for (var row = _scrollTop; row < _scrollBottom; row++)
            {
                _screen[row] = _screen[row + 1];
            }
            _screen[_scrollBottom] = BlankLine();
        }
        if (_history.Count > _maximumScrollbackLines)
        {
            _history.RemoveRange(0, _history.Count - _maximumScrollbackLines);
        }
    }

    private void ScrollDown(int amount)
    {
        amount = Math.Clamp(amount, 1, _scrollBottom - _scrollTop + 1);
        for (var count = 0; count < amount; count++)
        {
            for (var row = _scrollBottom; row > _scrollTop; row--)
            {
                _screen[row] = _screen[row - 1];
            }
            _screen[_scrollTop] = BlankLine();
        }
    }

    private void InsertCharacters(int amount)
    {
        amount = Math.Clamp(amount, 1, Columns - _column);
        Array.Copy(_screen[_row], _column, _screen[_row], _column + amount, Columns - _column - amount);
        for (var index = 0; index < amount; index++) _screen[_row][_column + index] = BlankCell();
    }

    private void DeleteCharacters(int amount)
    {
        amount = Math.Clamp(amount, 1, Columns - _column);
        Array.Copy(_screen[_row], _column + amount, _screen[_row], _column, Columns - _column - amount);
        for (var index = Columns - amount; index < Columns; index++) _screen[_row][index] = BlankCell();
    }

    private void EraseCharacters(int amount)
    {
        var end = Math.Min(Columns, _column + amount);
        for (var index = _column; index < end; index++) _screen[_row][index] = BlankCell();
    }

    private void InsertLines(int amount)
    {
        if (_row < _scrollTop || _row > _scrollBottom) return;
        amount = Math.Clamp(amount, 1, _scrollBottom - _row + 1);
        for (var row = _scrollBottom; row >= _row + amount; row--) _screen[row] = _screen[row - amount];
        for (var row = _row; row < _row + amount; row++) _screen[row] = BlankLine();
    }

    private void DeleteLines(int amount)
    {
        if (_row < _scrollTop || _row > _scrollBottom) return;
        amount = Math.Clamp(amount, 1, _scrollBottom - _row + 1);
        for (var row = _row; row <= _scrollBottom - amount; row++) _screen[row] = _screen[row + amount];
        for (var row = _scrollBottom - amount + 1; row <= _scrollBottom; row++) _screen[row] = BlankLine();
    }

    private void SetScrollRegion(int[] parameters)
    {
        var top = parameters.ElementAtOrDefault(0) is 0 ? 1 : parameters[0];
        var bottomValue = parameters.ElementAtOrDefault(1);
        var bottom = bottomValue == 0 ? Rows : bottomValue;
        if (top < bottom && top >= 1 && bottom <= Rows)
        {
            _scrollTop = top - 1;
            _scrollBottom = bottom - 1;
            _row = 0;
            _column = 0;
        }
    }

    private void EraseDisplay(int mode)
    {
        if (mode == 2 || mode == 3)
        {
            _screen = Enumerable.Range(0, Rows).Select(_ => BlankLine()).ToArray();
            if (mode == 3)
            {
                _history.Clear();
            }
            _row = 0;
            _column = 0;
            return;
        }
        if (mode == 1)
        {
            for (var row = 0; row < _row; row++) _screen[row] = BlankLine();
            for (var column = 0; column <= _column; column++) _screen[_row][column] = BlankCell();
            return;
        }
        for (var column = _column; column < Columns; column++) _screen[_row][column] = BlankCell();
        for (var row = _row + 1; row < Rows; row++) _screen[row] = BlankLine();
    }

    private void EraseLine(int mode)
    {
        var start = mode == 1 ? 0 : _column;
        var end = mode == 0 ? Columns - 1 : _column;
        if (mode == 2) { start = 0; end = Columns - 1; }
        for (var column = start; column <= end; column++) _screen[_row][column] = BlankCell();
    }

    private void ApplyGraphics(int[] parameters)
    {
        if (parameters.Length == 0) parameters = [0];
        for (var index = 0; index < parameters.Length; index++)
        {
            var code = parameters[index];
            switch (code)
            {
                case 0: _foreground = DefaultForeground; _background = DefaultBackground; _bold = false; break;
                case 1: _bold = true; break;
                case 22: _bold = false; break;
                case 39: _foreground = DefaultForeground; break;
                case 49: _background = DefaultBackground; break;
                case >= 30 and <= 37: _foreground = AnsiColors[code - 30]; break;
                case >= 40 and <= 47: _background = AnsiColors[code - 40]; break;
                case >= 90 and <= 97: _foreground = AnsiColors[code - 90 + 8]; break;
                case >= 100 and <= 107: _background = AnsiColors[code - 100 + 8]; break;
                case 38:
                case 48:
                    var color = ReadExtendedColor(parameters, ref index);
                    if (color is { } selected)
                    {
                        if (code == 38) _foreground = selected; else _background = selected;
                    }
                    break;
            }
        }
    }

    private static Color? ReadExtendedColor(int[] parameters, ref int index)
    {
        if (index + 2 < parameters.Length && parameters[index + 1] == 5)
        {
            index += 2;
            return IndexedColor(Math.Clamp(parameters[index], 0, 255));
        }
        if (index + 4 < parameters.Length && parameters[index + 1] == 2)
        {
            var red = Math.Clamp(parameters[index + 2], 0, 255);
            var green = Math.Clamp(parameters[index + 3], 0, 255);
            var blue = Math.Clamp(parameters[index + 4], 0, 255);
            index += 4;
            return Color.FromArgb(red, green, blue);
        }
        return null;
    }

    private static Color IndexedColor(int index)
    {
        if (index < 16) return AnsiColors[index];
        if (index >= 232)
        {
            var shade = 8 + (index - 232) * 10;
            return Color.FromArgb(shade, shade, shade);
        }
        var cube = index - 16;
        static int Component(int value) => value == 0 ? 0 : 55 + value * 40;
        return Color.FromArgb(Component(cube / 36), Component((cube / 6) % 6), Component(cube % 6));
    }

    private static int[] ParseParameters(string value)
    {
        value = value.TrimStart('?', '>', '!');
        if (value.Length == 0) return [];
        return value.Split(';').Select(part =>
            int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0).ToArray();
    }

    private void SaveCursor() { _savedRow = _row; _savedColumn = _column; }
    private void RestoreCursor() { _row = Math.Clamp(_savedRow, 0, Rows - 1); _column = Math.Clamp(_savedColumn, 0, Columns - 1); }

    private void Reset()
    {
        _history.Clear();
        _screen = Enumerable.Range(0, Rows).Select(_ => BlankLine()).ToArray();
        _row = 0;
        _column = 0;
        _foreground = DefaultForeground;
        _background = DefaultBackground;
        _bold = false;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    private TerminalCell[] BlankLine() => Enumerable.Repeat(BlankCell(), Columns).ToArray();
    private TerminalCell BlankCell() => new(' ', _foreground, _background, _bold);

    private enum ParserState { Normal, Escape, Csi, Osc, OscEscape }
    private readonly record struct TerminalCell(char Character, Color Foreground, Color Background, bool Bold)
    {
        internal bool SameStyle(TerminalCell other) =>
            Foreground == other.Foreground && Background == other.Background && Bold == other.Bold;
    }
}

internal sealed record VtTerminalSnapshot(string Text, IReadOnlyList<VtStyleRun> Runs, int CursorOffset);
internal sealed record VtStyleRun(int Start, int Length, Color Foreground, Color Background, bool Bold);
