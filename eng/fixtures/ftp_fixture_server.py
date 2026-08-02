import argparse
import os
from pathlib import Path

from OpenSSL import SSL
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler, TLS_FTPHandler
from pyftpdlib.handlers.ftp.data import DTPHandler
from pyftpdlib.handlers.ftps.data import TLS_DTPHandler
from pyftpdlib.servers import FTPServer


class ImplicitTLSHandler(TLS_FTPHandler):
    def handle(self) -> None:
        self.secure_connection(self.ssl_context)

    def handle_ssl_established(self) -> None:
        FTPHandler.handle(self)

    def ftp_AUTH(self, line: str) -> None:
        self.respond("503 Already using TLS.")


class ControlAuthenticatedTLSDataHandler(TLS_DTPHandler):
    def __init__(self, sock, command_channel) -> None:
        DTPHandler.__init__(self, sock, command_channel)
        if command_channel._prot:
            self.secure_connection(command_channel.data_ssl_context)


class MutualTLSHandler(TLS_FTPHandler):
    dtp_handler = ControlAuthenticatedTLSDataHandler
    data_ssl_context = None


def required_secret(name: str) -> str:
    value = os.environ.get(name)
    if not value or len(value) > 256 or any(ord(ch) < 32 for ch in value):
        raise RuntimeError(f"{name} is missing or invalid.")
    return value


def create_server_context(certificate: str, private_key: str) -> SSL.Context:
    private_key_password = required_secret("STORAGEHUB_FTP_SERVER_KEY_PASSWORD")
    context = SSL.Context(SSL.TLS_SERVER_METHOD)
    if hasattr(context, "set_min_proto_version"):
        context.set_min_proto_version(SSL.TLS1_2_VERSION)
    context.set_options(SSL.OP_NO_SSLv2 | SSL.OP_NO_SSLv3 | SSL.OP_NO_COMPRESSION)
    context.use_certificate_chain_file(str(Path(certificate).resolve()))
    context.set_passwd_cb(lambda _maximum_length, _verify, _user_data: private_key_password.encode("utf-8"))
    context.use_privatekey_file(str(Path(private_key).resolve()))
    context.check_privatekey()
    return context


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("plain", "explicit", "implicit"), required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--passive-ports", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--ready-file", required=True)
    parser.add_argument("--certificate")
    parser.add_argument("--private-key")
    parser.add_argument("--client-ca")
    parser.add_argument("--require-client-certificate", action="store_true")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    if not root.is_dir():
        raise RuntimeError("The FTP fixture root does not exist.")
    username = required_secret("STORAGEHUB_FTP_USERNAME")
    password = required_secret("STORAGEHUB_FTP_PASSWORD")
    passive_ports = [int(value) for value in args.passive_ports.split(",")]
    if not passive_ports or any(port < 1 or port > 65535 for port in passive_ports):
        raise RuntimeError("The passive port set is invalid.")

    authorizer = DummyAuthorizer()
    authorizer.add_user(username, password, str(root), perm="elradfmwMT")

    if args.mode == "plain":
        handler = FTPHandler
    else:
        if not args.certificate or not args.private_key:
            raise RuntimeError("FTPS requires a certificate and private key.")
        context = create_server_context(args.certificate, args.private_key)
        if args.require_client_certificate:
            if not args.client_ca:
                raise RuntimeError("Mutual TLS requires a client CA.")
            context.load_verify_locations(str(Path(args.client_ca).resolve()))
            context.set_verify(
                SSL.VERIFY_PEER | SSL.VERIFY_FAIL_IF_NO_PEER_CERT,
                lambda _connection, _certificate, _error_number, _depth, valid: valid,
            )
            handler = MutualTLSHandler
            handler.data_ssl_context = create_server_context(args.certificate, args.private_key)
        else:
            handler = ImplicitTLSHandler if args.mode == "implicit" else TLS_FTPHandler
        handler.ssl_context = context
        handler.tls_control_required = True
        handler.tls_data_required = True

    handler.authorizer = authorizer
    handler.banner = "StorageHub disposable FTP fixture ready."
    handler.passive_ports = passive_ports
    handler.masquerade_address = "127.0.0.1"
    handler.permit_foreign_addresses = False
    server = FTPServer(("127.0.0.1", args.port), handler)
    Path(args.ready_file).resolve().write_text("ready", encoding="ascii")
    server.serve_forever(timeout=0.25, blocking=True, handle_exit=True)


if __name__ == "__main__":
    main()
