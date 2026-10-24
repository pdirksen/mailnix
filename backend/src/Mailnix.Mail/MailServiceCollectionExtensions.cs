using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Mail;

public static class MailServiceCollectionExtensions
{
    public static IServiceCollection AddMailnixMail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MailOptions>().Bind(configuration.GetSection(MailOptions.Section));
        services.AddSingleton<IImapConnector, ImapConnector>();
        services.AddSingleton<IMailboxProbe, MailboxProbe>();
        services.AddSingleton<IMessageReader, MessageReader>();
        services.AddSingleton<IMailboxSessionFactory, ImapMailboxSessionFactory>();
        return services;
    }
}
