output "db_endpoint" {
  description = "The connection endpoint for the database"
  value       = aws_db_instance.postgres.endpoint
}

output "db_password_secret_arn" {
  description = "Secrets Manager ARN of the marten_app login used by the Lambdas"
  value       = aws_secretsmanager_secret.marten_app.arn
}