using AutomaticEnvelopes.Api.Features.Tenancy;

namespace AutomaticEnvelopes.Api.Features.Chat;

/// <summary>
/// Fixed first-contact notice. The model does not write it.
/// Spanish is the default: the bot does not classify the user's language.
/// Catalan and English are the same notice when a caller already knows the language.
/// </summary>
public static class FirstContactMessage
{
    public const string FallbackPrivacyPolicyUrl = "https://www.core-webhook.eu/privacy.html";

    public static string For(TenantProfile tenant) =>
        Compose(PreferredName(tenant), tenant.PrivacyPolicyUrl);

    public static string Compose(
        string? tenantName,
        string? privacyPolicyUrl,
        FirstContactLanguage language = FirstContactLanguage.Spanish)
    {
        var name = DisplayName(tenantName, language);
        var url = string.IsNullOrWhiteSpace(privacyPolicyUrl)
            ? FallbackPrivacyPolicyUrl
            : privacyPolicyUrl.Trim();

        return language switch
        {
            FirstContactLanguage.Catalan => Catalan(name, url),
            FirstContactLanguage.English => English(name, url),
            _ => Spanish(name, url)
        };
    }

    private static string PreferredName(TenantProfile tenant) =>
        string.IsNullOrWhiteSpace(tenant.Name) ? tenant.ShortName : tenant.Name;

    private static string DisplayName(string? tenantName, FirstContactLanguage language)
    {
        if (string.IsNullOrWhiteSpace(tenantName))
        {
            return FallbackName(language);
        }

        return string.Join(' ', tenantName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string FallbackName(FirstContactLanguage language) => language switch
    {
        FirstContactLanguage.Catalan => "l'organització",
        FirstContactLanguage.English => "the organisation",
        _ => "la organización"
    };

    private static string Spanish(string name, string url) =>
        $"""
        ¡Hola! 👋 Soy un asistente de inteligencia artificial y escribo en nombre de {name}.
        Aquí estoy para ayudarte.
        {name} es responsable de tus datos. Política de privacidad: {url}. Para borrarlos, escribe {BotPrompts.DeleteCommand}.
        """;

    private static string Catalan(string name, string url) =>
        $"""
        Hola! 👋 Sóc un assistent d'intel·ligència artificial i escric en nom de {name}.
        Sóc aquí per ajudar-te.
        {name} és responsable de les teves dades. Política de privacitat: {url}. Per esborrar-les, escriu {BotPrompts.DeleteCommand}.
        """;

    private static string English(string name, string url) =>
        $"""
        Hi! 👋 I'm an AI assistant writing on behalf of {name}.
        I'm here to help.
        {name} is responsible for your data. Privacy policy: {url}. To delete your data, send {BotPrompts.DeleteCommand}.
        """;
}

public enum FirstContactLanguage
{
    Spanish,
    Catalan,
    English
}
