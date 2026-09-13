#!/usr/bin/env python3
"""
Tiny command-line tool for poking a Denon/Marantz AV receiver over telnet (port 23).
Handy for trying out protocol commands before wiring them into the app.

Usage:
    py avr_cli.py --ip 192.168.1.50 PW?      # query, prints the receiver's replies
    py avr_cli.py --ip 192.168.1.50 MV50     # send a raw command (volume 50.0)
    py avr_cli.py --ip 192.168.1.50 MS?      # current sound mode
    py avr_cli.py --ip 192.168.1.50          # interactive: type commands line by line

The receiver expects ASCII commands terminated by CR (\\r). Replies arrive
asynchronously; this script listens for a short window (~1.2 s) and prints every
line it gets. Noisy unsolicited status lines (SSECOSTS/OPINF) are hidden.

The receiver accepts only one telnet client at a time — close the app first.
"""
import os
import socket
import sys
import time

PORT = 23
NOISE_PREFIXES = ("SSECOSTS", "OPINF")


def parse_args(argv):
    ip = os.environ.get("AVR_IP", "")
    args = list(argv)
    if args and args[0] == "--ip":
        if len(args) < 2:
            sys.exit("--ip needs an IP address")
        ip = args[1]
        args = args[2:]
    if not ip:
        sys.exit("Give the receiver's address with --ip <address> (or set AVR_IP).")
    return ip, args


def open_conn(ip):
    s = socket.create_connection((ip, PORT), timeout=4)
    s.settimeout(1.5)
    return s


def drain(sock, wait=1.2, show_noise=False):
    """Read for the given time and print the incoming lines."""
    time.sleep(wait)
    buf = b""
    try:
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            buf += chunk
    except socket.timeout:
        pass
    for line in buf.decode("ascii", "replace").split("\r"):
        line = line.strip()
        if not line:
            continue
        if not show_noise and line.startswith(NOISE_PREFIXES):
            continue
        print("  <", line)


def send(sock, cmd):
    print(">", cmd)
    sock.sendall((cmd + "\r").encode("ascii"))


def one_shot(ip, cmd):
    s = open_conn(ip)
    send(s, cmd)
    drain(s)
    s.close()


def interactive(ip):
    s = open_conn(ip)
    print(f"Connected to {ip}:{PORT}. Empty line / 'exit' quits; Ctrl+C works too.")
    print("Tip: PW?  MV?  SI?  MS?  SLP?  CV?  Z2?  - a question mark queries.")
    try:
        while True:
            try:
                cmd = input("> ").strip()
            except EOFError:
                break
            if cmd.lower() in ("", "exit", "quit"):
                break
            send(s, cmd)
            drain(s)
    except KeyboardInterrupt:
        pass
    finally:
        s.close()
        print("\nDisconnected.")


def main():
    ip, args = parse_args(sys.argv[1:])
    try:
        if not args:
            interactive(ip)
        else:
            one_shot(ip, " ".join(args))
    except OSError as e:
        sys.exit(f"Error: could not connect to {ip}:{PORT}: {e}")


if __name__ == "__main__":
    main()
