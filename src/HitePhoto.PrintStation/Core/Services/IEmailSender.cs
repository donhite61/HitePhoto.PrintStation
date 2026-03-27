using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.Shared.Models;

namespace HitePhoto.PrintStation.Core.Services;

public interface IEmailSender
{
    Task<EmailResult> SendOrderReadyEmailAsync(Order order, EmailTemplate? template = null);
}
