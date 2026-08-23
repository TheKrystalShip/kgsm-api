using System.Text;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// Turns the label somebody typed into a candidate instance id — the courtesy that lets a create form
/// ask for one thing ("Sunday Server") and still produce an id a person recognises in
/// <c>systemctl</c>, a journal grep and a <c>kgsm start</c> (<c>sunday-server</c>).
/// </summary>
/// <remarks>
/// <para>It is a <b>candidate</b>, never the answer. The engine owns both halves of that answer: it
/// validates the charset and it checks the roster, and <c>IInstanceService.GenerateId</c> is where both
/// happen. A slug this produces is offered there and used only if the engine echoes it back; a slug it
/// refuses costs the caller nothing, because the engine's own <c>blueprint</c>/<c>blueprint-NN</c> is
/// waiting behind it. That is why nothing here disambiguates a collision — inventing
/// <c>sunday-server-2</c> would be this API guessing at a roster it does not own.</para>
/// <para>The charset it targets is the engine's: <c>^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$</c>.</para>
/// </remarks>
public static class InstanceIdSlug
{
    /// <summary>The engine's id length limit — 64 characters.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The slug for <paramref name="displayName"/>, or <see langword="null"/> when the label yields
    /// nothing usable — an empty label, or one written entirely in characters the id charset has no
    /// place for (a name in Japanese, a row of emoji). Null means "let the engine mint one": there is no
    /// honest slug for those, and a placeholder would name a server after nothing anybody typed.
    /// </summary>
    public static string? From(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        var sb = new StringBuilder(displayName.Length);
        bool pendingSeparator = false;
        foreach (char c in displayName)
        {
            // ASCII only, and lower-cased: an id is typed at a shell and read out of a directory
            // listing, so it stays in the characters that survive both.
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                Append(c);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                Append(char.ToLowerInvariant(c));
            }
            else if (c is '.' or '_' or '-' or ' ')
            {
                // Runs of punctuation and whitespace collapse to one separator, and a trailing one is
                // never emitted — "My  Server!!" is `my-server`, not `my--server--`.
                pendingSeparator = sb.Length > 0;
            }
            else
            {
                pendingSeparator = sb.Length > 0;
            }
        }

        return sb.Length == 0 ? null : sb.ToString();

        void Append(char c)
        {
            if (sb.Length >= MaxLength) return;
            if (pendingSeparator && sb.Length < MaxLength - 1)
            {
                sb.Append('-');
                pendingSeparator = false;
            }

            sb.Append(c);
        }
    }
}
