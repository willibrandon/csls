use flate2::read::GzDecoder;
use rc_zip::EntryKind;
use rc_zip_sync::ReadZip;
use sha2::{Digest, Sha256};
use std::fmt::Write as _;
use std::fs::{self, File};
use std::io::{self, Read};
use std::path::Path;
use tar::Archive;
use zed_extension_api::{self as zed, Result, settings::LspSettings};

const LANGUAGE_SERVER_ID: &str = "csls";

struct CslsExtension {
    cached_binary_path: Option<String>,
}

impl zed::Extension for CslsExtension {
    fn new() -> Self {
        Self {
            cached_binary_path: None,
        }
    }

    fn language_server_command(
        &mut self,
        language_server_id: &zed::LanguageServerId,
        worktree: &zed::Worktree,
    ) -> Result<zed::Command> {
        if language_server_id.as_ref() != LANGUAGE_SERVER_ID {
            return Err(format!("unknown language server: {language_server_id}"));
        }

        let binary_settings = LspSettings::for_worktree(LANGUAGE_SERVER_ID, worktree)
            .ok()
            .and_then(|settings| settings.binary);
        let configured_arguments = binary_settings
            .as_ref()
            .and_then(|settings| settings.arguments.clone());
        if let Some(path) = binary_settings.and_then(|settings| settings.path) {
            return Ok(command(
                path,
                configured_arguments.unwrap_or_else(lsp_arguments),
            ));
        }

        if let Some(path) = worktree.which(LANGUAGE_SERVER_ID) {
            return Ok(command(
                path,
                configured_arguments.unwrap_or_else(lsp_arguments),
            ));
        }

        if let Some(path) = self.cached_binary_path.as_ref()
            && fs::metadata(path).is_ok_and(|metadata| metadata.is_file())
        {
            return Ok(command(
                path.clone(),
                configured_arguments.unwrap_or_else(lsp_arguments),
            ));
        }

        let path = install_latest_release(language_server_id)?;
        self.cached_binary_path = Some(path.clone());
        Ok(command(
            path,
            configured_arguments.unwrap_or_else(lsp_arguments),
        ))
    }

    fn language_server_workspace_configuration(
        &mut self,
        language_server_id: &zed::LanguageServerId,
        worktree: &zed::Worktree,
    ) -> Result<Option<zed::serde_json::Value>> {
        if language_server_id.as_ref() != LANGUAGE_SERVER_ID {
            return Ok(None);
        }

        let settings = LspSettings::for_worktree(LANGUAGE_SERVER_ID, worktree)
            .ok()
            .and_then(|settings| settings.settings);
        Ok(settings.map(|value| zed::serde_json::json!({ "csls": value })))
    }
}

fn command(path: String, args: Vec<String>) -> zed::Command {
    zed::Command {
        command: path,
        args,
        env: Default::default(),
    }
}

fn lsp_arguments() -> Vec<String> {
    vec!["lsp".to_owned()]
}

fn install_latest_release(language_server_id: &zed::LanguageServerId) -> Result<String> {
    zed::set_language_server_installation_status(
        language_server_id,
        &zed::LanguageServerInstallationStatus::CheckingForUpdate,
    );
    let release = zed::latest_github_release(
        "willibrandon/csls",
        zed::GithubReleaseOptions {
            require_assets: true,
            pre_release: false,
        },
    )?;
    let version = release
        .version
        .strip_prefix('v')
        .unwrap_or(&release.version);
    let (runtime_identifier, extension, file_type) = release_target()?;
    let archive_name = format!("csls-{version}-{runtime_identifier}.{extension}");
    let archive_asset = release
        .assets
        .iter()
        .find(|asset| asset.name == archive_name)
        .ok_or_else(|| format!("release {version} does not contain {archive_name}"))?;
    let checksum_asset = release
        .assets
        .iter()
        .find(|asset| asset.name == "SHA256SUMS")
        .ok_or_else(|| format!("release {version} does not contain SHA256SUMS"))?;
    let version_directory = format!("csls-{version}-{runtime_identifier}");
    let executable_name = if matches!(zed::current_platform().0, zed::Os::Windows) {
        "csls.exe"
    } else {
        "csls"
    };
    let executable_path = format!("{version_directory}/{executable_name}");
    if fs::metadata(&executable_path).is_ok_and(|metadata| metadata.is_file()) {
        return absolute_path(&executable_path);
    }

    zed::set_language_server_installation_status(
        language_server_id,
        &zed::LanguageServerInstallationStatus::Downloading,
    );
    let archive_path = format!(".{archive_name}.download");
    let checksum_path = format!(".SHA256SUMS-{version}.download");
    zed::download_file(
        &checksum_asset.download_url,
        &checksum_path,
        zed::DownloadedFileType::Uncompressed,
    )?;
    zed::download_file(
        &archive_asset.download_url,
        &archive_path,
        zed::DownloadedFileType::Uncompressed,
    )?;
    let result = verify_and_extract(
        &archive_path,
        &archive_name,
        &checksum_path,
        &version_directory,
        file_type,
    );
    fs::remove_file(&archive_path).ok();
    fs::remove_file(&checksum_path).ok();
    result?;
    if fs::metadata(&executable_path).is_err() {
        fs::remove_dir_all(&version_directory).ok();
        return Err(format!("{archive_name} does not contain {executable_name}"));
    }

    if !matches!(zed::current_platform().0, zed::Os::Windows) {
        zed::make_file_executable(&executable_path)?;
    }

    remove_outdated_versions(&version_directory)?;
    zed::set_language_server_installation_status(
        language_server_id,
        &zed::LanguageServerInstallationStatus::None,
    );
    absolute_path(&executable_path)
}

#[derive(Clone, Copy)]
enum ArchiveType {
    GzipTar,
    Zip,
}

fn release_target() -> Result<(&'static str, &'static str, ArchiveType)> {
    match zed::current_platform() {
        (zed::Os::Windows, zed::Architecture::X8664) => Ok(("win-x64", "zip", ArchiveType::Zip)),
        (zed::Os::Windows, zed::Architecture::Aarch64) => {
            Ok(("win-arm64", "zip", ArchiveType::Zip))
        }
        (zed::Os::Linux, zed::Architecture::X8664) => {
            Ok(("linux-x64", "tar.gz", ArchiveType::GzipTar))
        }
        (zed::Os::Linux, zed::Architecture::Aarch64) => {
            Ok(("linux-arm64", "tar.gz", ArchiveType::GzipTar))
        }
        (zed::Os::Mac, zed::Architecture::X8664) => Ok(("osx-x64", "tar.gz", ArchiveType::GzipTar)),
        (zed::Os::Mac, zed::Architecture::Aarch64) => {
            Ok(("osx-arm64", "tar.gz", ArchiveType::GzipTar))
        }
        _ => Err("csls does not publish an asset for this platform".to_owned()),
    }
}

fn verify_and_extract(
    archive_path: &str,
    archive_name: &str,
    checksum_path: &str,
    destination_path: &str,
    archive_type: ArchiveType,
) -> Result<()> {
    let checksums = fs::read_to_string(checksum_path)
        .map_err(|error| format!("failed to read SHA256SUMS: {error}"))?;
    let expected_checksum = checksums
        .lines()
        .filter_map(|line| line.split_once("  "))
        .find_map(|(checksum, name)| (name == archive_name).then_some(checksum))
        .ok_or_else(|| format!("SHA256SUMS does not contain {archive_name}"))?;
    let actual_checksum = hash_file(archive_path)?;
    if !actual_checksum.eq_ignore_ascii_case(expected_checksum) {
        return Err(format!("checksum verification failed for {archive_name}"));
    }

    fs::remove_dir_all(destination_path).ok();
    fs::create_dir_all(destination_path)
        .map_err(|error| format!("failed to create {destination_path}: {error}"))?;
    let extraction = match archive_type {
        ArchiveType::GzipTar => extract_tar(archive_path, destination_path),
        ArchiveType::Zip => extract_zip(archive_path, destination_path),
    };
    if extraction.is_err() {
        fs::remove_dir_all(destination_path).ok();
    }

    extraction
}

fn hash_file(path: &str) -> Result<String> {
    let mut file = File::open(path).map_err(|error| format!("failed to open {path}: {error}"))?;
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let count = file
            .read(&mut buffer)
            .map_err(|error| format!("failed to read {path}: {error}"))?;
        if count == 0 {
            break;
        }

        hasher.update(&buffer[..count]);
    }

    let digest = hasher.finalize();
    let mut checksum = String::with_capacity(digest.len() * 2);
    for byte in digest {
        write!(&mut checksum, "{byte:02x}")
            .map_err(|error| format!("failed to format the checksum: {error}"))?;
    }

    Ok(checksum)
}

fn extract_tar(archive_path: &str, destination_path: &str) -> Result<()> {
    let file = File::open(archive_path)
        .map_err(|error| format!("failed to open {archive_path}: {error}"))?;
    Archive::new(GzDecoder::new(file))
        .unpack(destination_path)
        .map_err(|error| format!("failed to extract {archive_path}: {error}"))
}

fn extract_zip(archive_path: &str, destination_path: &str) -> Result<()> {
    let bytes = fs::read(archive_path)
        .map_err(|error| format!("failed to read {archive_path}: {error}"))?;
    let archive = bytes
        .read_zip()
        .map_err(|error| format!("failed to parse {archive_path}: {error}"))?;
    for entry in archive.entries() {
        let relative_path = entry
            .sanitized_name()
            .ok_or_else(|| format!("{archive_path} contains an unsafe path"))?;
        let destination = Path::new(destination_path).join(relative_path);
        match entry.kind() {
            EntryKind::Directory => fs::create_dir_all(&destination)
                .map_err(|error| format!("failed to create {}: {error}", destination.display()))?,
            EntryKind::File => {
                if let Some(parent) = destination.parent() {
                    fs::create_dir_all(parent).map_err(|error| {
                        format!("failed to create {}: {error}", parent.display())
                    })?;
                }

                let mut output = File::create(&destination).map_err(|error| {
                    format!("failed to create {}: {error}", destination.display())
                })?;
                io::copy(&mut entry.reader(), &mut output).map_err(|error| {
                    format!("failed to extract {}: {error}", destination.display())
                })?;
            }
            EntryKind::Symlink => {
                return Err(format!("{archive_path} contains a symbolic link"));
            }
        }
    }

    Ok(())
}

fn absolute_path(path: &str) -> Result<String> {
    std::env::current_dir()
        .map(|directory| directory.join(path).to_string_lossy().into_owned())
        .map_err(|error| format!("failed to resolve {path}: {error}"))
}

fn remove_outdated_versions(current_directory: &str) -> Result<()> {
    let entries = fs::read_dir(".")
        .map_err(|error| format!("failed to list the extension directory: {error}"))?;
    for entry in entries {
        let entry = entry.map_err(|error| format!("failed to read an extension entry: {error}"))?;
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if name.starts_with("csls-") && name != current_directory {
            fs::remove_dir_all(entry.path()).ok();
        }
    }

    Ok(())
}

zed::register_extension!(CslsExtension);
