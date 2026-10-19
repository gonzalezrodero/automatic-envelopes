provider "aws" {
  region = var.aws_region
}

# Password comes from PGPASSWORD. The Database workflow exports it; the PR plan role cannot.
provider "postgresql" {
  host            = aws_db_instance.postgres.address
  port            = aws_db_instance.postgres.port
  database        = aws_db_instance.postgres.db_name
  username        = aws_db_instance.postgres.username
  sslmode         = "require"
  superuser       = false
  connect_timeout = 15
}
