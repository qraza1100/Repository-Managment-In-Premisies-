using QualityAviationHelpdesk.Contracts;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Services;

namespace QualityAviationHelpdesk.Tests
{
    public class FieldRulesTests
    {
        private static readonly string[] RequesterOnly = { FieldAudience.Requester };
        private static readonly string[] Both = { FieldAudience.Requester, FieldAudience.It };

        private static List<FieldDefinition> Definitions() => new()
        {
            new() { Key = "system", Label = "System", Kind = FieldKinds.Select, Options = "Q+ CMR\nTraacs\nEmail", Required = true, Active = true, Audience = FieldAudience.Requester, MaxLength = 200, SortOrder = 1 },
            new() { Key = "steps", Label = "Steps", Kind = FieldKinds.TextArea, Required = true, Active = true, Audience = FieldAudience.Requester, AppliesTo = "Bug / error", MaxLength = 50, SortOrder = 2 },
            new() { Key = "hours", Label = "Estimate", Kind = FieldKinds.Number, Active = true, Audience = FieldAudience.It, MaxLength = 20, SortOrder = 3 },
            new() { Key = "when", Label = "When", Kind = FieldKinds.Date, Active = true, Audience = FieldAudience.Requester, MaxLength = 20, SortOrder = 4 },
            new() { Key = "tags", Label = "Areas", Kind = FieldKinds.MultiSelect, Options = "Refunds\nInvoices\nReports", Active = true, Audience = FieldAudience.Requester, MaxLength = 500, SortOrder = 5 },
            new() { Key = "urgent", Label = "Urgent", Kind = FieldKinds.Checkbox, Active = true, Audience = FieldAudience.Requester, MaxLength = 10, SortOrder = 6 },
            new() { Key = "old", Label = "Old", Kind = FieldKinds.Text, Required = true, Active = false, Audience = FieldAudience.Requester, MaxLength = 10, SortOrder = 7 }
        };

        [Fact]
        public void Valid_input_is_kept_in_its_normal_form()
        {
            var errors = new List<string>();
            var clean = FieldRules.Validate(Definitions(), "Bug / error", Both, new Dictionary<string, string>
            {
                ["system"] = "traacs",
                ["steps"] = "Open refund, press save",
                ["hours"] = "2.50",
                ["when"] = "2026-11-19",
                ["tags"] = "invoices|Refunds",
                ["urgent"] = "true"
            }, errors);

            Assert.Empty(errors);
            Assert.Equal("Traacs", clean["system"]);
            Assert.Equal("2.50", clean["hours"]);
            Assert.Equal("Invoices | Refunds", clean["tags"]);
            Assert.Equal("Yes", clean["urgent"]);
        }

        [Fact]
        public void Required_fields_apply_only_to_their_ticket_types()
        {
            var errors = new List<string>();
            FieldRules.Validate(Definitions(), "Automation request", RequesterOnly, new Dictionary<string, string> { ["system"] = "Email" }, errors);
            Assert.Empty(errors);

            FieldRules.Validate(Definitions(), "Bug / error", RequesterOnly, new Dictionary<string, string> { ["system"] = "Email" }, errors);
            Assert.Single(errors);
            Assert.Contains("Steps", errors[0]);
        }

        [Fact]
        public void A_requester_cannot_fill_IT_fields_or_hidden_fields_or_invent_fields()
        {
            var errors = new List<string>();
            var clean = FieldRules.Validate(Definitions(), "Other", RequesterOnly, new Dictionary<string, string>
            {
                ["system"] = "Email",
                ["hours"] = "999",
                ["old"] = "x",
                ["injected"] = "<script>"
            }, errors);

            Assert.Empty(errors);
            Assert.False(clean.ContainsKey("hours"));
            Assert.False(clean.ContainsKey("old"));
            Assert.False(clean.ContainsKey("injected"));
        }

        [Theory]
        [InlineData("system", "Fax machine", "choose one")]
        [InlineData("when", "19/11/2026", "date")]
        [InlineData("tags", "Refunds|Payroll", "choose from")]
        public void Wrong_values_are_explained(string key, string value, string expected)
        {
            var errors = new List<string>();
            var input = new Dictionary<string, string> { ["system"] = "Email", [key] = value };
            FieldRules.Validate(Definitions(), "Other", Both, input, errors);
            Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Length_limits_hold()
        {
            var errors = new List<string>();
            FieldRules.Validate(Definitions(), "Bug / error", RequesterOnly,
                new Dictionary<string, string> { ["system"] = "Email", ["steps"] = new string('x', 51) }, errors);
            Assert.Contains(errors, e => e.Contains("at most 50"));
        }

        [Fact]
        public void New_definitions_are_checked_before_they_are_stored()
        {
            var types = new[] { "Bug / error", "Other" };
            Assert.Empty(FieldRules.CheckDefinition(new FieldDefinitionDto { Key = "contact_phone", Label = "Phone", Kind = FieldKinds.Text, MaxLength = 40 }, types, isNew: true));

            Assert.NotEmpty(FieldRules.CheckDefinition(new FieldDefinitionDto { Key = "Bad Key", Label = "x", Kind = FieldKinds.Text }, types, true));
            Assert.NotEmpty(FieldRules.CheckDefinition(new FieldDefinitionDto { Key = "pick", Label = "x", Kind = FieldKinds.Select }, types, true));
            Assert.NotEmpty(FieldRules.CheckDefinition(new FieldDefinitionDto { Key = "pick", Label = "x", Kind = FieldKinds.Select, Options = new() { "a", "A" } }, types, true));
            Assert.NotEmpty(FieldRules.CheckDefinition(new FieldDefinitionDto { Key = "pick", Label = "x", Kind = "html" }, types, true));
            Assert.NotEmpty(FieldRules.CheckDefinition(new FieldDefinitionDto { Key = "pick", Label = "x", Kind = FieldKinds.Text, AppliesTo = new() { "Unknown" } }, types, true));
        }
    }
}
