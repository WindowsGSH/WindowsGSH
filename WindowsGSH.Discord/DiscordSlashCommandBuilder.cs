using Discord;
using WindowsGSH.Core.Discord;

namespace WindowsGSH.Discord;

internal static class DiscordSlashCommandBuilder
{
    public static ApplicationCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(DiscordSlashCommands.RootName)
            .WithDescription("Manage WindowsGSH game servers.")
            .WithContextTypes(InteractionContextType.Guild);

        foreach (var definition in DiscordSlashCommands.Definitions)
        {
            var subcommand = new SlashCommandOptionBuilder()
                .WithName(definition.Name)
                .WithDescription(definition.Description)
                .WithType(ApplicationCommandOptionType.SubCommand);

            if (definition.HasServerOption)
            {
                subcommand.AddOption(new SlashCommandOptionBuilder()
                    .WithName("server")
                    .WithDescription(definition.ServerRequired
                        ? "The server to manage."
                        : "Optional server; omit it for app-wide output.")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(definition.ServerRequired)
                    .WithAutocomplete(true));
            }

            if (definition.HasCommandTextOption)
            {
                subcommand.AddOption(new SlashCommandOptionBuilder()
                    .WithName("command")
                    .WithDescription("The command text to send.")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)
                    .WithMaxLength(1000));
            }

            if (definition.HasConfirmationOption)
            {
                subcommand.AddOption(new SlashCommandOptionBuilder()
                    .WithName("confirm")
                    .WithDescription("Confirm the pending Stop All request.")
                    .WithType(ApplicationCommandOptionType.Boolean)
                    .WithRequired(false));
            }

            builder.AddOption(subcommand);
        }

        return builder.Build();
    }
}
