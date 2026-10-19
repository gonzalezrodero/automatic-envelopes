provider "aws" {
  region = var.aws_region
}

provider "postgresql" {
  host            = aws_db_instance.postgres.address
  port            = aws_db_instance.postgres.port
  database        = aws_db_instance.postgres.db_name
  username        = aws_db_instance.postgres.username
  password        = var.db_master_password
  sslmode         = "verify-full"
  sslrootcert     = "/tmp/rds-global-bundle.pem"
  superuser       = false
  connect_timeout = 15
}
