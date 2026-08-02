import argparse
import datetime
import ipaddress
import os
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID


def write_private(path: Path, key: rsa.RSAPrivateKey, password: str) -> None:
    path.write_bytes(
        key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8,
            serialization.BestAvailableEncryption(password.encode("utf-8")),
        )
    )


def build_leaf(
    common_name: str,
    issuer: x509.Certificate,
    issuer_key: rsa.RSAPrivateKey,
    usage: x509.ObjectIdentifier,
) -> tuple[rsa.RSAPrivateKey, x509.Certificate]:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    now = datetime.datetime.now(datetime.UTC)
    builder = (
        x509.CertificateBuilder()
        .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, common_name)]))
        .issuer_name(issuer.subject)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(minutes=5))
        .not_valid_after(now + datetime.timedelta(days=2))
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(x509.ExtendedKeyUsage([usage]), critical=False)
    )
    if usage == ExtendedKeyUsageOID.SERVER_AUTH:
        builder = builder.add_extension(
            x509.SubjectAlternativeName(
                [
                    x509.DNSName("localhost"),
                    x509.IPAddress(ipaddress.ip_address("127.0.0.1")),
                ]
            ),
            critical=False,
        )
    return key, builder.sign(issuer_key, hashes.SHA256())


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)

    pfx_password = os.environ.get("STORAGEHUB_FTP_CLIENT_PFX_PASSWORD")
    if not pfx_password or len(pfx_password) > 256 or any(ord(ch) < 32 for ch in pfx_password):
        raise RuntimeError("The fixture PFX password is missing or invalid.")
    server_key_password = os.environ.get("STORAGEHUB_FTP_SERVER_KEY_PASSWORD")
    if (
        not server_key_password
        or len(server_key_password) > 256
        or any(ord(ch) < 32 for ch in server_key_password)
    ):
        raise RuntimeError("The fixture server-key password is missing or invalid.")

    ca_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    now = datetime.datetime.now(datetime.UTC)
    ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "StorageHub FTP fixture CA")])
    ca_certificate = (
        x509.CertificateBuilder()
        .subject_name(ca_name)
        .issuer_name(ca_name)
        .public_key(ca_key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(minutes=5))
        .not_valid_after(now + datetime.timedelta(days=2))
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .add_extension(
            x509.KeyUsage(
                digital_signature=True,
                content_commitment=False,
                key_encipherment=False,
                data_encipherment=False,
                key_agreement=False,
                key_cert_sign=True,
                crl_sign=True,
                encipher_only=False,
                decipher_only=False,
            ),
            critical=True,
        )
        .sign(ca_key, hashes.SHA256())
    )
    server_key, server_certificate = build_leaf(
        "StorageHub FTP fixture server",
        ca_certificate,
        ca_key,
        ExtendedKeyUsageOID.SERVER_AUTH,
    )
    client_key, client_certificate = build_leaf(
        "StorageHub FTP fixture client",
        ca_certificate,
        ca_key,
        ExtendedKeyUsageOID.CLIENT_AUTH,
    )

    (output / "ca.pem").write_bytes(ca_certificate.public_bytes(serialization.Encoding.PEM))
    (output / "server.pem").write_bytes(
        server_certificate.public_bytes(serialization.Encoding.PEM)
        + ca_certificate.public_bytes(serialization.Encoding.PEM)
    )
    write_private(output / "server-key.pem", server_key, server_key_password)
    (output / "client.pfx").write_bytes(
        pkcs12.serialize_key_and_certificates(
            b"storagehub-ftp-fixture",
            client_key,
            client_certificate,
            [ca_certificate],
            serialization.BestAvailableEncryption(pfx_password.encode("utf-8")),
        )
    )
    (output / "server.sha256").write_text(
        server_certificate.fingerprint(hashes.SHA256()).hex().upper(),
        encoding="ascii",
    )


if __name__ == "__main__":
    main()
