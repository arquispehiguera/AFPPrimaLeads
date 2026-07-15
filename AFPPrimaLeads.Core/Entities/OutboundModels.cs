namespace AFPPrimaLeads.Core.Entities
{
    public class OutboundRequest
    {
        public Scope scope { get; set; } = new();
        public string batchId { get; set; } = string.Empty;
        public string campaignId { get; set; } = string.Empty;
        public OutboundConfiguration configuration { get; set; } = new();
    }

    public class Scope
    {
        public List<Contact> contacts { get; set; } = new();
    }

    public class Contact
    {
        public string id { get; set; } = string.Empty;
    }

    public class OutboundConfiguration
    {
        public Address address { get; set; } = new();
        public string agent { get; set; } = string.Empty;
        public string scheduleDate { get; set; } = string.Empty;
        public int priority { get; set; } = 100;
        public ContactData contactData { get; set; } = new();
    }

    public class Address
    {
        public string Type { get; set; } = "Phone";
        public string Kind { get; set; } = "CELLULAR";
        public List<string> Channels { get; set; } = new() { "CALL" };
        public string Number { get; set; } = string.Empty;
    }

    public class ContactData
    {
        public string Name { get; set; } = string.Empty;
        public string ImportId { get; set; } = string.Empty;
        public List<NameValue>? NameValuesSearchText { get; set; }
    }

    public class NameValue
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class SetSkillsRequest
    {
        public bool mix { get; set; }
        public string contactId { get; set; } = string.Empty;
        public List<SkillItem> skills { get; set; } = new();
    }

    public class SkillItem
    {
        public string SkillId { get; set; } = string.Empty;
        public string? Mode { get; set; }
        public int Value { get; set; }
    }
}
