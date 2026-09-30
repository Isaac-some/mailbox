SHELL := /bin/sh

check:
	npm run check

test:
	npm test

build:
	npm run build

keys:
	mkdir -p secrets
	[ -e secrets/credential.key ] || openssl rand -base64 32 | tr '+/' '-_' | tr -d '=' > secrets/credential.key
	[ -e secrets/session.key ] || openssl rand -base64 32 | tr '+/' '-_' | tr -d '=' > secrets/session.key
	chmod 600 secrets/*.key

backup:
	@test -n "$(BACKUP_DIR)"
	mkdir -p "$(BACKUP_DIR)"
	docker compose exec -T postgres pg_dump -U mailbox -d mailbox --format=custom > "$(BACKUP_DIR)/mailbox-$$(date +%Y%m%d-%H%M%S).dump"
	tar -czf "$(BACKUP_DIR)/mailbox-secrets-$$(date +%Y%m%d-%H%M%S).tgz" secrets

restore:
	@test -n "$(DUMP)"
	docker compose exec -T postgres pg_restore -U mailbox -d mailbox --clean --if-exists < "$(DUMP)"
