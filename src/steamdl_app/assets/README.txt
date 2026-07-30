本目录在 CI 构建时注入运行时资源:
- cacert.pem: Mozilla CA 证书包(curl.se/ca/cacert.pem),供 Android 上的
  .NET bionic 引擎通过 SSL_CERT_FILE 使用。

本地不提交这些文件,见 ../.gitignore。
