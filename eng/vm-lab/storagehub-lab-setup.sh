#!/bin/bash
set -euo pipefail

useradd --create-home --shell /bin/bash storagehub || true
echo 'storagehub:__FTP_PASSWORD__' | chpasswd
install -d -m 700 -o storagehub -g storagehub /home/storagehub/.ssh
printf '%s\n' '__CLIENT_PUBLIC_KEY__' > /home/storagehub/.ssh/authorized_keys
chown storagehub:storagehub /home/storagehub/.ssh/authorized_keys
chmod 600 /home/storagehub/.ssh/authorized_keys
install -d -m 755 -o storagehub -g storagehub /home/storagehub/mounted
install -d -m 755 -o storagehub -g storagehub /mounted
ssh-keygen -q -t ed25519 -N '' -f /etc/ssh/storagehub_rotated_ed25519

for mode in password publickey rotated; do
  port=2222
  password_auth=yes
  publickey_auth=no
  host_key=/etc/ssh/ssh_host_ed25519_key
  if [ "$mode" = publickey ]; then port=2223; password_auth=no; publickey_auth=yes; fi
  if [ "$mode" = rotated ]; then port=2224; host_key=/etc/ssh/storagehub_rotated_ed25519; fi
  cat > "/etc/ssh/sshd_config_storagehub_$mode" <<EOF
Port $port
ListenAddress 0.0.0.0
PidFile /run/sshd-storagehub-$mode.pid
HostKey $host_key
PasswordAuthentication $password_auth
PubkeyAuthentication $publickey_auth
KbdInteractiveAuthentication no
UsePAM yes
PermitRootLogin no
AllowUsers storagehub
AuthorizedKeysFile .ssh/authorized_keys
Subsystem sftp internal-sftp
EOF
  cat > "/etc/systemd/system/storagehub-sshd-$mode.service" <<EOF
[Unit]
Description=StorageHub lab SSH ($mode)
After=network.target ssh.service
[Service]
ExecStart=/usr/sbin/sshd -D -e -f /etc/ssh/sshd_config_storagehub_$mode
Restart=on-failure
[Install]
WantedBy=multi-user.target
EOF
done

install -d -m 700 /etc/storagehub-lab
openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj '/CN=StorageHub Lab CA' -keyout /etc/storagehub-lab/ca.key -out /etc/storagehub-lab/ca.crt
openssl req -newkey rsa:2048 -nodes -subj '/CN=storagehub-debian13' -keyout /etc/storagehub-lab/server.key -out /etc/storagehub-lab/server.csr
openssl x509 -req -days 3650 -in /etc/storagehub-lab/server.csr -CA /etc/storagehub-lab/ca.crt -CAkey /etc/storagehub-lab/ca.key -CAcreateserial -out /etc/storagehub-lab/server.crt
openssl x509 -in /etc/storagehub-lab/server.crt -outform DER -out /etc/storagehub-lab/server.der
openssl req -newkey rsa:2048 -nodes -subj '/CN=StorageHub Lab Client' -keyout /etc/storagehub-lab/client.key -out /etc/storagehub-lab/client.csr
openssl x509 -req -days 3650 -in /etc/storagehub-lab/client.csr -CA /etc/storagehub-lab/ca.crt -CAkey /etc/storagehub-lab/ca.key -CAcreateserial -out /etc/storagehub-lab/client.crt
openssl pkcs12 -export -out /etc/storagehub-lab/client.pfx -inkey /etc/storagehub-lab/client.key -in /etc/storagehub-lab/client.crt -certfile /etc/storagehub-lab/ca.crt -passout pass:__PFX_PASSWORD__
chmod 600 /etc/storagehub-lab/*

LAB_IP=$(hostname -I | awk '{print $1}')
make_vsftpd() {
  mode="$1"; port="$2"; min_port="$3"; max_port="$4"; tls="$5"; implicit="$6"; mtls="$7"
  cat > "/etc/vsftpd-storagehub-$mode.conf" <<EOF
listen=YES
listen_ipv6=NO
listen_port=$port
anonymous_enable=NO
local_enable=YES
write_enable=YES
local_umask=022
utf8_filesystem=YES
chroot_local_user=YES
allow_writeable_chroot=YES
local_root=/home/storagehub
pam_service_name=vsftpd
pasv_enable=YES
pasv_address=$LAB_IP
pasv_min_port=$min_port
pasv_max_port=$max_port
ssl_enable=$tls
rsa_cert_file=/etc/storagehub-lab/server.crt
rsa_private_key_file=/etc/storagehub-lab/server.key
ca_certs_file=/etc/storagehub-lab/ca.crt
force_local_logins_ssl=$tls
force_local_data_ssl=$tls
ssl_tlsv1=YES
ssl_sslv2=NO
ssl_sslv3=NO
implicit_ssl=$implicit
require_cert=$mtls
validate_cert=$mtls
EOF
  cat > "/etc/systemd/system/storagehub-vsftpd-$mode.service" <<EOF
[Unit]
Description=StorageHub lab FTP ($mode)
After=network.target
[Service]
ExecStart=/usr/sbin/vsftpd /etc/vsftpd-storagehub-$mode.conf
Restart=on-failure
[Install]
WantedBy=multi-user.target
EOF
}
make_vsftpd plain 2121 30000 30009 NO NO NO
make_vsftpd explicit 2122 30010 30019 YES NO NO
make_vsftpd implicit 2990 30020 30029 YES YES NO
make_vsftpd mtls 2124 30030 30039 YES NO YES

curl --fail --location --retry 5 --output /usr/local/bin/minio https://dl.min.io/server/minio/release/linux-amd64/minio
chmod 755 /usr/local/bin/minio
useradd --system --home-dir /var/lib/storagehub-minio --shell /usr/sbin/nologin minio || true
install -d -m 750 -o minio -g minio /var/lib/storagehub-minio/data
cat > /etc/systemd/system/storagehub-minio.service <<EOF
[Unit]
Description=StorageHub lab MinIO
After=network-online.target
Wants=network-online.target
[Service]
User=minio
Group=minio
Environment=MINIO_ROOT_USER=__MINIO_ACCESS_KEY__
Environment=MINIO_ROOT_PASSWORD=__MINIO_SECRET_KEY__
Environment=MINIO_BROWSER=off
ExecStart=/usr/local/bin/minio server --address :9000 /var/lib/storagehub-minio/data
Restart=on-failure
[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable storagehub-sshd-password storagehub-sshd-publickey storagehub-sshd-rotated
systemctl enable storagehub-vsftpd-plain storagehub-vsftpd-explicit storagehub-vsftpd-implicit storagehub-vsftpd-mtls
systemctl enable storagehub-minio
systemctl start storagehub-sshd-password storagehub-sshd-publickey storagehub-sshd-rotated
systemctl start storagehub-vsftpd-plain storagehub-vsftpd-explicit storagehub-vsftpd-implicit storagehub-vsftpd-mtls
systemctl start storagehub-minio
install -d -m 755 /var/lib/storagehub-lab
date --iso-8601=seconds > /var/lib/storagehub-lab/ready
