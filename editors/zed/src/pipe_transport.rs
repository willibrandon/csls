use zed_extension_api::{self as zed, Result};

const DEBUGGER_COMMAND_TOKEN: &str = "${debuggerCommand}";
const MAXIMUM_ARGUMENTS: usize = 64;
const MAXIMUM_ENVIRONMENT_ENTRIES: usize = 128;

/// The local pipe program that carries DAP to the debugger in the target environment.
pub(crate) struct PipeTransportCommand {
    pub(crate) command: String,
    pub(crate) arguments: Vec<String>,
    pub(crate) envs: Vec<(String, String)>,
    pub(crate) cwd: Option<String>,
}

pub(crate) fn create(value: &zed::serde_json::Value) -> Result<PipeTransportCommand> {
    let object = value
        .as_object()
        .ok_or_else(|| "pipeTransport must be an object".to_owned())?;
    for name in object.keys() {
        if !matches!(
            name.as_str(),
            "pipeProgram"
                | "pipeArgs"
                | "debuggerPath"
                | "debuggerRuntimePath"
                | "pipeCwd"
                | "pipeEnv"
                | "commandShell"
        ) {
            return Err(format!("pipeTransport.{name} is not a supported property"));
        }
    }

    let command = required_text(object.get("pipeProgram"), "pipeTransport.pipeProgram")?;
    let debugger_path = required_text(object.get("debuggerPath"), "pipeTransport.debuggerPath")?;
    let debugger_runtime_path = optional_text(
        object.get("debuggerRuntimePath"),
        "pipeTransport.debuggerRuntimePath",
    )?;
    let cwd = optional_text(object.get("pipeCwd"), "pipeTransport.pipeCwd")?;
    let managed_launcher = debugger_path.to_ascii_lowercase().ends_with(".dll");
    if debugger_runtime_path.is_some() && !managed_launcher {
        return Err(
            "pipeTransport.debuggerRuntimePath applies only to a .dll debuggerPath".to_owned(),
        );
    }
    let shell = object
        .get("commandShell")
        .map_or(Some("posix"), |value| value.as_str());
    let shell = match shell {
        Some("posix") => "posix",
        Some("powershell") => "powershell",
        _ => return Err("pipeTransport.commandShell must be posix or powershell".to_owned()),
    };

    let raw_args = object.get("pipeArgs");
    let empty_args = Vec::new();
    let args = match raw_args {
        Some(value) => value
            .as_array()
            .ok_or_else(|| "pipeTransport.pipeArgs must be an array of strings".to_owned())?,
        None => &empty_args,
    };
    if args.len() > MAXIMUM_ARGUMENTS {
        return Err(format!(
            "pipeTransport.pipeArgs exceeds {MAXIMUM_ARGUMENTS} arguments"
        ));
    }

    let mut envs = Vec::new();
    if let Some(environment) = object.get("pipeEnv") {
        let entries = environment
            .as_object()
            .ok_or_else(|| "pipeTransport.pipeEnv must be an object".to_owned())?;
        if entries.len() > MAXIMUM_ENVIRONMENT_ENTRIES {
            return Err(format!(
                "pipeTransport.pipeEnv exceeds {MAXIMUM_ENVIRONMENT_ENTRIES} entries"
            ));
        }
        for (name, value) in entries {
            if name.is_empty() || name.contains('=') || has_control_character(name) {
                return Err("pipeTransport.pipeEnv contains an invalid variable name".to_owned());
            }
            let value = argument_text(Some(value), &format!("pipeTransport.pipeEnv.{name}"))?;
            envs.push((name.clone(), value.to_owned()));
        }
    }

    let mut debugger_parts = Vec::with_capacity(4);
    if managed_launcher {
        debugger_parts.push(debugger_runtime_path.unwrap_or("dotnet"));
        debugger_parts.push(debugger_path);
    } else {
        debugger_parts.push(debugger_path);
    }
    debugger_parts.extend(["debugger", "dap"]);
    let debugger_command = if shell == "powershell" {
        format!(
            "& {}",
            debugger_parts
                .into_iter()
                .map(quote_powershell)
                .collect::<Vec<_>>()
                .join(" ")
        )
    } else {
        debugger_parts
            .into_iter()
            .map(quote_posix)
            .collect::<Vec<_>>()
            .join(" ")
    };

    let mut substitutions = 0;
    let mut arguments = Vec::with_capacity(args.len() + 1);
    for value in args {
        let argument = argument_text(Some(value), "pipeTransport.pipeArgs")?;
        if argument == DEBUGGER_COMMAND_TOKEN {
            substitutions += 1;
            arguments.push(debugger_command.clone());
        } else if argument.contains(DEBUGGER_COMMAND_TOKEN) {
            return Err(
                "pipeTransport.pipeArgs must use ${debuggerCommand} as a whole argument".to_owned(),
            );
        } else {
            arguments.push(argument.to_owned());
        }
    }
    if substitutions > 1 {
        return Err("pipeTransport.pipeArgs may contain ${debuggerCommand} only once".to_owned());
    }
    if substitutions == 0 {
        arguments.push(debugger_command);
    }
    Ok(PipeTransportCommand {
        command: command.to_owned(),
        arguments,
        envs,
        cwd: cwd.map(str::to_owned),
    })
}

fn required_text<'a>(value: Option<&'a zed::serde_json::Value>, name: &str) -> Result<&'a str> {
    let text = argument_text(value, name)?;
    if text.trim().is_empty() {
        return Err(format!("{name} must be a nonempty string"));
    }
    Ok(text)
}

fn optional_text<'a>(
    value: Option<&'a zed::serde_json::Value>,
    name: &str,
) -> Result<Option<&'a str>> {
    value
        .map(|value| required_text(Some(value), name))
        .transpose()
}

fn argument_text<'a>(value: Option<&'a zed::serde_json::Value>, name: &str) -> Result<&'a str> {
    let text = value
        .and_then(zed::serde_json::Value::as_str)
        .ok_or_else(|| format!("{name} must be a string"))?;
    if text.encode_utf16().count() > 32768 || has_control_character(text) {
        return Err(format!(
            "{name} must be a string without control characters of at most 32768 characters"
        ));
    }
    Ok(text)
}

fn has_control_character(value: &str) -> bool {
    value.bytes().any(|byte| byte < 0x20 || byte == 0x7f)
}

fn quote_posix(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\\''"))
}

fn quote_powershell(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}
