using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QualityAviationHelpdesk.Services
{
    // Checks the run-time fields of a ticket against their definitions. Whatever the form or the client sends,
    // only defined, active, applicable fields are kept, each in its own normal form.
    public static partial class FieldRules
    {
        public const string MultiSeparator = " | ";

        [GeneratedRegex("^[a-z][a-z0-9_]{1,59}$")]
        public static partial Regex KeyPattern();

        // audiences: which fields the caller may fill (requester form: requester only; IT: both)
        public static Dictionary<string, string> Validate(IEnumerable<FieldDefinition> definitions, string ticketType, ICollection<string> audiences,
            IDictionary<string, string> input, List<string> errors)
        {
            input ??= new Dictionary<string, string>();
            var clean = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var field in definitions.Where(f => f.Active && audiences.Contains(f.Audience) && f.AppliesToType(ticketType)).OrderBy(f => f.SortOrder))
            {
                input.TryGetValue(field.Key, out var raw);
                var value = (raw ?? "").Trim();

                if (value.Length == 0 || (field.Kind == FieldKinds.Checkbox && value.ToLowerInvariant() is "false" or "off" or "no" or "0"))
                {
                    if (field.Required && field.Kind != FieldKinds.Checkbox) errors.Add($"{field.Label} is required.");
                    else if (field.Required) errors.Add($"{field.Label} must be ticked.");
                    else if (field.Kind == FieldKinds.Checkbox) clean[field.Key] = "No";
                    continue;
                }

                if (value.Length > Math.Clamp(field.MaxLength, 1, 4000))
                {
                    errors.Add($"{field.Label} can be at most {field.MaxLength} characters.");
                    continue;
                }

                var options = field.OptionList();
                switch (field.Kind)
                {
                    case FieldKinds.Number:
                        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                        {
                            errors.Add($"{field.Label} must be a number.");
                            continue;
                        }
                        value = number.ToString(CultureInfo.InvariantCulture);
                        break;

                    case FieldKinds.Date:
                        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        {
                            errors.Add($"{field.Label} must be a date (yyyy-mm-dd).");
                            continue;
                        }
                        value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        break;

                    case FieldKinds.Select:
                        var match = options.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
                        if (match == null)
                        {
                            errors.Add($"{field.Label}: choose one of the listed options.");
                            continue;
                        }
                        value = match;
                        break;

                    case FieldKinds.MultiSelect:
                        var chosen = value.Split(new[] { '|', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(v => options.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        if (chosen.Count == 0 || chosen.Any(c => c == null))
                        {
                            errors.Add($"{field.Label}: choose from the listed options.");
                            continue;
                        }
                        value = string.Join(MultiSeparator, chosen.Distinct());
                        break;

                    case FieldKinds.Checkbox:
                        value = "Yes";
                        break;

                    case FieldKinds.Text:
                        value = value.Replace('\r', ' ').Replace('\n', ' ');
                        break;
                }

                clean[field.Key] = value;
            }

            return clean;
        }

        // A definition coming from the IT client, before it is stored. Returns what is wrong with it.
        public static List<string> CheckDefinition(FieldDefinitionDto field, ICollection<string> ticketTypes, bool isNew)
        {
            var errors = new List<string>();
            if (isNew && !KeyPattern().IsMatch(field.Key ?? ""))
                errors.Add("The key must start with a letter and use only lower-case letters, digits and _ (2 to 60 characters), e.g. \"contact_phone\".");
            if (string.IsNullOrWhiteSpace(field.Label) || field.Label.Trim().Length > 120)
                errors.Add("The label is required (at most 120 characters).");
            if (!FieldKinds.All.Contains(field.Kind))
                errors.Add($"The kind must be one of: {string.Join(", ", FieldKinds.All)}.");
            if (field.Audience is not (FieldAudience.Requester or FieldAudience.It))
                errors.Add("The field is either for the requester (web form) or for IT (client only).");
            if (field.MaxLength is < 1 or > 4000)
                errors.Add("The maximum length must be between 1 and 4000.");
            if ((field.HelpText ?? "").Length > 300)
                errors.Add("The help text can be at most 300 characters.");

            var options = (field.Options ?? new()).Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).ToList();
            if (FieldKinds.HasOptions(field.Kind))
            {
                if (options.Count == 0) errors.Add("A choice field needs at least one option.");
                if (options.Count > 50) errors.Add("A choice field can have at most 50 options.");
                if (options.Any(o => o.Length > 120 || o.Contains('|'))) errors.Add("Options can be at most 120 characters and cannot contain |.");
                if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count) errors.Add("Every option must be different.");
            }

            var unknown = (field.AppliesTo ?? new()).Where(t => !ticketTypes.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0) errors.Add($"Unknown ticket type(s): {string.Join(", ", unknown)}.");
            return errors;
        }
    }
}
