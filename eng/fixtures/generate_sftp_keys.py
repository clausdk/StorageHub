import argparse
import base64
import hashlib
import os
from pathlib import Path

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa


def required_secret(name: str) -> str:
    value = os.environ.get(name)
    if not value or len(value) > 256 or any(ord(character) < 32 for character in value):
        raise RuntimeError(f"{name} is missing or invalid.")
    return value


def write_private_key(path: Path, passphrase: str) -> rsa.RSAPrivateKey:
    key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    path.write_bytes(
        key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.OpenSSH,
            serialization.BestAvailableEncryption(passphrase.encode("utf-8")),
        )
    )
    return key


def write_public_key(path: Path, key: rsa.RSAPrivateKey) -> None:
    path.write_bytes(
        key.public_key().public_bytes(
            serialization.Encoding.OpenSSH,
            serialization.PublicFormat.OpenSSH,
        )
        + b"\n"
    )


def write_fingerprint(path: Path, key: rsa.RSAPrivateKey) -> None:
    encoded = key.public_key().public_bytes(
        serialization.Encoding.OpenSSH,
        serialization.PublicFormat.OpenSSH,
    ).split()[1]
    wire_key = base64.b64decode(encoded, validate=True)
    path.write_text(hashlib.sha256(wire_key).hexdigest().upper(), encoding="ascii")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)

    host_passphrase = required_secret("STORAGEHUB_SFTP_HOST_KEY_PASSPHRASE")
    client_passphrase = required_secret("STORAGEHUB_SFTP_CLIENT_KEY_PASSPHRASE")
    alternate_passphrase = required_secret("STORAGEHUB_SFTP_ALTERNATE_KEY_PASSPHRASE")

    host_key = write_private_key(output / "host.key", host_passphrase)
    rotated_host_key = write_private_key(output / "rotated-host.key", host_passphrase)
    client_key = write_private_key(output / "client.key", client_passphrase)
    alternate_client_key = write_private_key(output / "alternate-client.key", alternate_passphrase)

    write_public_key(output / "client.pub", client_key)
    write_public_key(output / "alternate-client.pub", alternate_client_key)
    write_fingerprint(output / "host.sha256", host_key)
    write_fingerprint(output / "rotated-host.sha256", rotated_host_key)


if __name__ == "__main__":
    main()
