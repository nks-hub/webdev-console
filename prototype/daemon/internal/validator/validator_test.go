package validator

import (
	"testing"
)

const validApacheConf = `<VirtualHost *:443>
    ServerName myapp.test
    DocumentRoot "C:\work\sites\myapp\www"
    SSLEngine on
</VirtualHost>`

const missingDocRootConf = `<VirtualHost *:80>
    ServerName myapp.test
</VirtualHost>`

const mismatchedTagsConf = `<VirtualHost *:80>
    ServerName foo.test
    DocumentRoot "C:\x"
`

const validNginxConf = `server {
    listen 443 ssl;
    server_name myapp.test;
    root "C:\work\sites\myapp\www";
    location / { try_files $uri /index.php; }
}`

const badNginxConf = `server {
    listen 80;
    server_name bad.test;
    location / { try_files $uri /index.php;
`

func TestApacheHeuristic_Valid(t *testing.T) {
	v := NewApacheValidator("")
	result := v.ValidateBytes([]byte(validApacheConf))
	if !result.Valid {
		t.Errorf("expected valid, got error: %v | output: %s", result.Err, result.Output)
	}
}

func TestApacheHeuristic_MissingDocumentRoot(t *testing.T) {
	v := NewApacheValidator("")
	result := v.ValidateBytes([]byte(missingDocRootConf))
	if result.Valid {
		t.Error("expected invalid config to fail")
	}
}

func TestApacheHeuristic_MismatchedTags(t *testing.T) {
	v := NewApacheValidator("")
	result := v.ValidateBytes([]byte(mismatchedTagsConf))
	if result.Valid {
		t.Error("expected mismatched tags to fail")
	}
}

func TestNginxHeuristic_Valid(t *testing.T) {
	v := NewNginxValidator("")
	result := v.ValidateBytes([]byte(validNginxConf))
	if !result.Valid {
		t.Errorf("expected valid, got error: %v | output: %s", result.Err, result.Output)
	}
}

func TestNginxHeuristic_MismatchedBraces(t *testing.T) {
	v := NewNginxValidator("")
	result := v.ValidateBytes([]byte(badNginxConf))
	if result.Valid {
		t.Error("expected unbalanced braces to fail")
	}
}

func TestValidateBytes_EmptyConfig(t *testing.T) {
	v := NewApacheValidator("")
	result := v.ValidateBytes([]byte(""))
	if result.Valid {
		t.Error("empty config should fail apache heuristic")
	}
}
