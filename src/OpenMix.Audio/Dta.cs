namespace OpenMix.Audio;

// Minimal parser for Harmonix DTA / FUSION s-expressions:
//   (keymap (keyzone (sample_path "..") (root_note 41) ...) ...)
// A node is a list whose elements are atoms (string) or child Dta nodes.
public sealed class Dta
{
    public List<object> Items { get; } = new();          // string | Dta

    public string? Head => Items.Count > 0 ? Items[0] as string : null;

    public IEnumerable<Dta> Children => Items.OfType<Dta>();

    // direct child list whose head symbol == name
    public Dta? Child(string name) =>
        Children.FirstOrDefault(c => c.Head == name);

    public IEnumerable<Dta> ChildrenNamed(string name) =>
        Children.Where(c => c.Head == name);

    // (name <value>) -> the value atom after the head
    public string? Value(string name)
    {
        var c = Child(name);
        return c is { Items.Count: >= 2 } ? c.Items[1] as string : null;
    }

    public int Int(string name, int def = 0) =>
        int.TryParse(Value(name), out var v) ? v : def;

    public double Dbl(string name, double def = 0) =>
        double.TryParse(Value(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;

    public static Dta Parse(string text)
    {
        int i = 0;
        // wrap top-level (may be multiple top forms) in a synthetic root
        var root = new Dta();
        SkipWs(text, ref i);
        while (i < text.Length)
        {
            if (text[i] == '(') root.Items.Add(ParseList(text, ref i));
            else { ReadAtom(text, ref i); }   // stray atom at top: ignore
            SkipWs(text, ref i);
        }
        return root;
    }

    public static Dta ParseFile(string path) => Parse(File.ReadAllText(path));

    static Dta ParseList(string s, ref int i)
    {
        i++; // consume '('
        var node = new Dta();
        SkipWs(s, ref i);
        while (i < s.Length && s[i] != ')')
        {
            if (s[i] == '(') node.Items.Add(ParseList(s, ref i));
            else node.Items.Add(ReadAtom(s, ref i));
            SkipWs(s, ref i);
        }
        if (i < s.Length) i++; // consume ')'
        return node;
    }

    static string ReadAtom(string s, ref int i)
    {
        if (s[i] == '"')
        {
            i++;
            int start = i;
            while (i < s.Length && s[i] != '"') i++;
            string str = s.Substring(start, i - start);
            if (i < s.Length) i++; // closing quote
            return str;
        }
        int a = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '(' && s[i] != ')') i++;
        return s.Substring(a, i - a);
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == ';') { while (i < s.Length && s[i] != '\n') i++; continue; } // ; comment
            break;
        }
    }
}
