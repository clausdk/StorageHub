import argparse
import asyncio
import hmac
import os
from pathlib import Path

import asyncssh


def required_secret(name: str) -> str:
    value = os.environ.get(name)
    if not value or len(value) > 256 or any(ord(character) < 32 for character in value):
        raise RuntimeError(f"{name} is missing or invalid.")
    return value


class FixtureSshServer(asyncssh.SSHServer):
    def __init__(self, mode: str, username: str, password: str, authorized_key) -> None:
        self._mode = mode
        self._username = username
        self._password = password
        self._authorized_key = authorized_key

    def begin_auth(self, username: str) -> bool:
        return True

    def password_auth_supported(self) -> bool:
        return self._mode == "password"

    def validate_password(self, username: str, password: str) -> bool:
        return (
            self._mode == "password"
            and hmac.compare_digest(username, self._username)
            and hmac.compare_digest(password, self._password)
        )

    def public_key_auth_supported(self) -> bool:
        return self._mode == "public-key"

    def validate_public_key(self, username: str, key) -> bool:
        return (
            self._mode == "public-key"
            and hmac.compare_digest(username, self._username)
            and hmac.compare_digest(
                key.export_public_key("openssh"),
                self._authorized_key.export_public_key("openssh"),
            )
        )


async def serve(args) -> None:
    root = Path(args.root).resolve()
    if not root.is_dir():
        raise RuntimeError("The SFTP fixture root does not exist.")
    host_key_path = Path(args.host_key).resolve()
    authorized_key_path = Path(args.authorized_key).resolve()
    if not host_key_path.is_file() or not authorized_key_path.is_file():
        raise RuntimeError("The SFTP fixture key material is missing.")

    username = required_secret("STORAGEHUB_SFTP_USERNAME")
    password = required_secret("STORAGEHUB_SFTP_PASSWORD")
    host_passphrase = required_secret("STORAGEHUB_SFTP_HOST_KEY_PASSPHRASE")
    host_key = asyncssh.read_private_key(str(host_key_path), passphrase=host_passphrase)
    authorized_key = asyncssh.read_public_key(str(authorized_key_path))

    server = await asyncssh.listen(
        "127.0.0.1",
        args.port,
        server_factory=lambda: FixtureSshServer(
            args.mode,
            username,
            password,
            authorized_key,
        ),
        server_host_keys=[host_key],
        sftp_factory=lambda channel: asyncssh.SFTPServer(channel, chroot=str(root)),
        encoding=None,
    )
    Path(args.ready_file).resolve().write_text("ready", encoding="ascii")
    await server.wait_closed()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("password", "public-key"), required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--ready-file", required=True)
    parser.add_argument("--host-key", required=True)
    parser.add_argument("--authorized-key", required=True)
    args = parser.parse_args()
    if args.port < 1 or args.port > 65535:
        raise RuntimeError("The SFTP fixture port is invalid.")
    asyncio.run(serve(args))


if __name__ == "__main__":
    main()
