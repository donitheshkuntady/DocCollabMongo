using DocCollabMongoApi.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace DocCollabMongoApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class DocumentCollabController : ControllerBase
    {
        private readonly IHubContext<DocumentEditorHub> _hubContext;
        private readonly ILogger<DocumentCollabController> _logger;

        public DocumentCollabController(IHubContext<DocumentEditorHub> hubContext, ILogger<DocumentCollabController> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
        }

        [HttpGet("{documentCollabId}/download")]
        public Task<string> Download
            ([FromServices] DocumentCollabDownloadHandler handler, string documentCollabId) => handler.DownloadAsync(documentCollabId);

        [HttpPost]
        [Route("ImportFile")]
        public Task<string> ImportFile([FromServices] DocumentCollabWriteHandler handler, FileCollabDetails fileInfo) => handler.ImportFileAsync(fileInfo);

        [HttpPost("updateAction")]
        public async Task<ActionInfo?> UpdateAction([FromServices] DocumentCollabWriteHandler handler, [FromBody] ActionInfo param)
        {
            _logger.LogMessage(logLevel: LogLevel.Information, message: $"ActionInfo for RoomName: {param.RoomName}, for User: {param.CurrentUser} - {param}");
            var modifiedAction = await handler.UpdateActionAsync(param);
            await _hubContext.Clients.Group(param.RoomName).SendAsync("dataReceived", "action", modifiedAction);
            return modifiedAction;
        }

        [HttpPost("getActionsFromServer")]
        public Task<string> GetActionsFromServer([FromServices] DocumentCollabWriteHandler handler, [FromBody] ActionInfo param) => handler.GetActionsFromServerAsync(param);
    }
}
