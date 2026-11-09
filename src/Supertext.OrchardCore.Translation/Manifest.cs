using OrchardCore.Modules.Manifest;

[assembly: Module(
    Name = "Supertext Translation",
    Author = "Supertext AG",
    Website = "https://www.supertext.com",
    Version = "1.0.0",
    Description = "Translate content items into other cultures with Supertext AI translation.",
    Dependencies = ["OrchardCore.ContentLocalization", "OrchardCore.Settings"],
    Category = "Internationalization"
)]
