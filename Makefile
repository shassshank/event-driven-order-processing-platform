.PHONY: restore build test-no-docker up test-after-docker smoke dlq demo down clean final-check

restore:
	dotnet restore

build:
	dotnet build

test-no-docker:
	bash scripts/test-no-docker.sh --collect:"XPlat Code Coverage"

up:
	docker compose -f deploy/docker-compose.yml up -d --build postgres redis rabbitmq order-service inventory-service payment-service notification-service reporting-service api-gateway

test-after-docker:
	bash scripts/test-after-docker.sh --collect:"XPlat Code Coverage"

smoke:
	bash scripts/platform-smoke-test.sh

dlq:
	python3 scripts/dlq-inspect.py

demo:
	bash scripts/live-demo.sh

down:
	docker compose -f deploy/docker-compose.yml down -v

clean:
	dotnet clean

final-check: clean restore build test-no-docker up test-after-docker
