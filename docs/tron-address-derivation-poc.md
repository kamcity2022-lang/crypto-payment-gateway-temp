# TRON Address Derivation PoC & Verification

## Overview
This document proves deterministic, non-custodial address derivation for TRON (TRC-20) using public-only derivation material (BIP-44).

## Derivation Specifications
- **Standard:** BIP-44 / BIP-32
- **Path:** `m/44'/195'/0'/0/{index}`
- **Input:** Account-level Extended Public Key (`xpub`)
- **Key Extraction:** Uncompressed 64-byte public key (skipping 0x04 prefix)
- **Hashing:** Pure Keccak-256 standard (matching TRON/EVM specification)
- **Payload:** Address prefix `0x41` prepended to the last 20 bytes of Keccak-256 hash (21 bytes total)
- **Encoding:** Double SHA-256 checksum with Base58Check encoding

## Security Guarantees
1. **Zero Private Key Exposure:** The gateway server holds only the account-level `xpub`. Private keys or mnemonic seeds are neither accepted nor stored.
2. **Determinism:** Independent instances derive identical TRON addresses starting with `T` for any given index.