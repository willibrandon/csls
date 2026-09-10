use zed_extension_api::{
    self as zed, DebugAdapterBinary, DebugTaskDefinition, Result, StartDebuggingRequestArguments,
    StartDebuggingRequestArgumentsRequest,
};

pub(crate) fn binary(path: String, config: DebugTaskDefinition) -> Result<DebugAdapterBinary> {
    let configuration = zed::serde_json::from_str(&config.config)
        .map_err(|error| format!("invalid csls debug configuration: {error}"))?;
    let request = request_kind(&configuration)?;
    Ok(DebugAdapterBinary {
        command: Some(path),
        arguments: vec!["debugger".to_owned(), "dap".to_owned()],
        envs: Default::default(),
        cwd: None,
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
