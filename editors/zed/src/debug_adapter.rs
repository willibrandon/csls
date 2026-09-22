use zed_extension_api::{
    self as zed, DebugAdapterBinary, DebugTaskDefinition, Result, StartDebuggingRequestArguments,
    StartDebuggingRequestArgumentsRequest,
};

use crate::pipe_transport;

pub(crate) fn binary(
    path: Option<String>,
    config: DebugTaskDefinition,
    configuration: zed::serde_json::Value,
) -> Result<DebugAdapterBinary> {
    let request = request_kind(&configuration)?;
    let transport = configuration
        .get("pipeTransport")
        .map(pipe_transport::create)
        .transpose()?;
    let (command, arguments, envs, cwd) = match transport {
        Some(transport) => (
            transport.command,
            transport.arguments,
            transport.envs,
            transport.cwd,
        ),
        None => (
            path.ok_or_else(|| "csls debug adapter path was not resolved".to_owned())?,
            vec!["debugger".to_owned(), "dap".to_owned()],
            Default::default(),
            None,
        ),
    };
    Ok(DebugAdapterBinary {
        command: Some(command),
        arguments,
        envs,
        cwd,
        connection: None,
        request_args: StartDebuggingRequestArguments {
            configuration: config.config,
            request,
        },
    })
}

pub(crate) fn request_kind(
    config: &zed::serde_json::Value,
) -> Result<StartDebuggingRequestArgumentsRequest> {
    match config
        .get("request")
        .and_then(zed::serde_json::Value::as_str)
    {
        Some("launch") => Ok(StartDebuggingRequestArgumentsRequest::Launch),
        Some("attach") => Ok(StartDebuggingRequestArgumentsRequest::Attach),
        Some(request) => Err(format!("unsupported csls debug request: {request}")),
        None => Err("csls debug configuration requires request to be launch or attach".to_owned()),
    }
}
