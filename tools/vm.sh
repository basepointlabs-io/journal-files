#!/usr/bin/env bash
# Sync the project to the Windows VM and run commands there (the VM's default shell is cmd.exe).
#
#   tools/vm.sh sync              copy the working tree (no samples/, .git, bin/, obj/) to the VM
#   tools/vm.sh run "<command>"   run a cmd.exe command inside the project folder on the VM
#
# VM_IP overrides the address when the VM gets a new DHCP lease.
set -euo pipefail

VM_HOST="${VM_HOST:-flexi-vm}"
VM_IP="${VM_IP:-192.168.229.148}"
VM_DIR='%USERPROFILE%\dev\revit-journal-analyzer'
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

ssh_vm() { ssh -o HostName="$VM_IP" "$VM_HOST" "$@"; }

case "${1:-}" in
  sync)
    ssh_vm "if not exist \"$VM_DIR\" mkdir \"$VM_DIR\""
    COPYFILE_DISABLE=1 tar -C "$ROOT" --no-mac-metadata \
      --exclude ./samples --exclude ./.git --exclude bin --exclude obj --exclude .DS_Store \
      -czf - . | ssh_vm "tar -xzf - -C \"$VM_DIR\""
    ;;
  run)
    shift
    ssh_vm "cd /d \"$VM_DIR\" && $*"
    ;;
  *)
    echo "usage: $0 sync | run \"<command>\"" >&2
    exit 2
    ;;
esac
