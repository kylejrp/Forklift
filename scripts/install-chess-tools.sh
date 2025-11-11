#!/usr/bin/env bash
set -euo pipefail

log() {
  echo "[install-chess-tools] $*"
}

ensure_command() {
  command -v "$1" >/dev/null 2>&1
}

ordo_banner() {
  if ! ensure_command ordo; then
    return 1
  fi
  ordo -h 2>&1 | grep -m1 -E '[[:alnum:]]'
}

APT_UPDATED=0
PREFER_APT="${USE_APT_CUTECHESS:-0}"

apt_update_once() {
  if [[ "${APT_UPDATED}" -eq 0 ]]; then
    log "Refreshing apt package index..."
    sudo apt-get update
    APT_UPDATED=1
  fi
}

install_packages() {
  if ! ensure_command apt-get; then
    return 1
  fi
  if [[ $# -eq 0 ]]; then
    return 0
  fi
  apt_update_once
  sudo apt-get install -y --no-install-recommends "$@"
}

cleanup_tempdir() {
  local dir="$1"
  if [[ -n "$dir" && -d "$dir" ]]; then
    rm -rf "$dir"
  fi
}

cutechess_ready() {
  if ! ensure_command cutechess-cli; then
    return 1
  fi
  if cutechess-cli --version >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

install_cutechess() {
  if cutechess_ready; then
    log "cutechess-cli already installed: $(cutechess-cli --version | head -n1)"
    return 0
  fi

  if ensure_command cutechess-cli; then
    log "Removing non-functional cutechess-cli binary"
    sudo rm -f "$(command -v cutechess-cli)"
  fi

  if [[ "${PREFER_APT}" == "1" ]]; then
    if ensure_command apt-get; then
      log "USE_APT_CUTECHESS=1 set; attempting apt installation of cutechess-cli first"
      if install_packages cutechess; then
        if [[ -x /usr/games/cutechess-cli ]]; then
          sudo install -m 0755 /usr/games/cutechess-cli /usr/local/bin/cutechess-cli
        fi
        if cutechess_ready; then
          log "cutechess-cli installed via apt preference: $(cutechess-cli --version | head -n1)"
          return 0
        fi
        log "Apt installation did not yield a working cutechess-cli; falling back to release download."
      else
        log "Apt installation of cutechess failed; falling back to release download." >&2
      fi
    else
      log "apt-get not available despite USE_APT_CUTECHESS=1; continuing with release download." >&2
    fi
  fi

  if ! ensure_command curl; then
    log "curl is required to download cutechess-cli." >&2
    return 1
  fi

  # Ensure tools exist
  for cmd in curl jq; do
    command -v "$cmd" >/dev/null 2>&1 || { log "$cmd is required"; return 1; }
  done

  log "Fetching latest cutechess release metadata (curl + jq)..."
  local asset_url asset_kind

  # Pull latest release JSON
  json=$(curl -fsSL "https://api.github.com/repos/cutechess/cutechess/releases/latest") || {
    log "GitHub API request failed"; return 1;
  }

  # Choose preferred asset in order
  asset_url=$(jq -r '
    .assets[]?.browser_download_url as $u
    | ($u | ascii_downcase) as $n
    | {u:$u, n:$n}
    | select((.n|contains("linux")) and (.n|contains("cli")))
    | .u' <<<"$json" |
    head -n1)

  if [[ -z $asset_url ]]; then
    asset_url=$(jq -r '
      .assets[]?.browser_download_url
      | select((ascii_downcase|endswith(".appimage")) and (ascii_downcase|contains("x86_64")))
    ' <<<"$json" | head -n1)
  fi
  if [[ -z $asset_url ]]; then
    asset_url=$(jq -r '.assets[]?.browser_download_url | select(ascii_downcase|contains("linux"))' <<<"$json" | head -n1)
  fi

  if [[ -z $asset_url ]]; then
    log "No suitable cutechess asset found"; return 1
  fi

  asset_kind=$([[ "${asset_url,,}" == *.appimage ]] && echo appimage || echo archive)


  local installed_via_release=false

  if [[ -n "${asset_url}" ]]; then
    log "Downloading cutechess asset from ${asset_url}"
    local archive
    archive="$tmpdir/$(basename "$asset_url")"
    curl -L "${asset_url}" -o "${archive}"

    case "${asset_kind}" in
      appimage)
        chmod +x "${archive}"
        (cd "$tmpdir" && "${archive}" --appimage-extract >/dev/null 2>&1)
        if [[ -f "$tmpdir/squashfs-root/usr/bin/cutechess-cli" ]]; then
          log "Installing cutechess-cli extracted from AppImage"
          sudo rm -rf /opt/cutechess
          sudo mkdir -p /opt/cutechess
          sudo cp -r "$tmpdir/squashfs-root/usr" /opt/cutechess/
          sudo ln -sf /opt/cutechess/usr/bin/cutechess-cli /usr/local/bin/cutechess-cli
          sudo chmod +x /opt/cutechess/usr/bin/cutechess-cli
          installed_via_release=true
        else
          log "AppImage did not contain cutechess-cli." >&2
        fi
        ;;
      *)
        local extract_dir="$tmpdir/extracted"
        mkdir -p "$extract_dir"
        case "${archive}" in
          *.tar.bz2|*.tbz2)
            tar -xjf "${archive}" -C "$extract_dir"
            ;;
          *.tar.xz)
            tar -xJf "${archive}" -C "$extract_dir"
            ;;
          *.tar.gz|*.tgz)
            tar -xzf "${archive}" -C "$extract_dir"
            ;;
          *.zip)
            unzip -q "${archive}" -d "$extract_dir"
            ;;
          *)
            log "Unknown archive format for cutechess asset." >&2
            extract_dir=""
            ;;
        esac
        if [[ -n "$extract_dir" && -d "$extract_dir" ]]; then
          local bin_path
          bin_path=$(find "$extract_dir" -type f -name 'cutechess-cli' | head -n1)
          if [[ -n "$bin_path" ]]; then
            log "Installing cutechess-cli to /usr/local/bin"
            sudo install -m 0755 "$bin_path" /usr/local/bin/cutechess-cli
            installed_via_release=true
          else
            log "Unable to locate cutechess-cli binary inside archive." >&2
          fi
        fi
        ;;
    esac
  fi

  trap - RETURN
  cleanup_tempdir "${tmpdir}"

  if ! cutechess_ready; then
    if ensure_command apt-get; then
      if [[ "${installed_via_release}" == true ]]; then
        log "cutechess release binary may require Qt runtime libraries; installing via apt"
        install_packages qtbase5-runtime || log "Qt runtime installation failed via apt." >&2
      else
        log "Attempting to install cutechess-cli via apt"
      fi

      if ! cutechess_ready; then
        if apt-cache show cutechess >/dev/null 2>&1; then
          if install_packages cutechess; then
            if [[ -x /usr/games/cutechess-cli ]]; then
              sudo install -m 0755 /usr/games/cutechess-cli /usr/local/bin/cutechess-cli
            fi
          else
            log "Failed to install cutechess package via apt." >&2
          fi
        else
          log "cutechess package not available via apt repositories." >&2
        fi
      fi
    else
      log "cutechess-cli installation failed and no fallback available." >&2
      return 1
    fi
  fi

  if ! cutechess_ready; then
    log "cutechess-cli installation completed but binary is still not functional." >&2
    return 1
  fi

  log "cutechess-cli installed: $(cutechess-cli --version | head -n1)"
}

install_ordo() {
  if ensure_command ordo && ordo -h >/dev/null 2>&1; then
    log "Ordo already installed: $(ordo_banner)"
    return 0
  fi

  if ! ensure_command git; then
    log "git is required to build Ordo." >&2
    return 1
  fi

  local tmpdir
  tmpdir=$(mktemp -d)
  trap 'cleanup_tempdir "${tmpdir}"' RETURN

  log "Cloning Ordo repository..."
  git clone --depth=1 https://github.com/michiguel/Ordo.git "$tmpdir/Ordo"
  pushd "$tmpdir/Ordo" >/dev/null
  make -j"$(nproc)"
  sudo make install
  popd >/dev/null

  trap - RETURN
  cleanup_tempdir "${tmpdir}"

  if ! (ensure_command ordo && ordo -h >/dev/null 2>&1); then
    log "Failed to install Ordo." >&2
    return 1
  fi

  log "Ordo installed: $(ordo_banner)"
}

main() {
  install_cutechess
  install_ordo
  log "cutechess-cli version: $(cutechess-cli --version | head -n1)"
  log "Ordo version: $(ordo_banner)"
}

main "$@"
