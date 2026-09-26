variable "aws_region" {
  description = "AWS region to deploy to"
  type        = string
}

variable "project_name" {
  description = "Name of the project"
  type        = string
}

variable "db_instance_class" {
  description = "The instance type of the RDS instance"
  type        = string
}

variable "multi_az" {
  description = "Specifies if the RDS instance is multi-AZ"
  type        = bool
}

variable "allocated_storage" {
  description = "The allocated storage in gigabytes"
  type        = number
}

variable "backup_retention_period" {
  description = "Days of automated RDS backups to keep (0 disables automated backups)"
  type        = number
  default     = 0
}

variable "rds_auto_start" {
  description = "If true, EventBridge starts RDS at 08:00 Europe/Madrid. Keep false while the app is unused."
  type        = bool
  default     = false
}

variable "aws_account_id" {
  type    = string
  default = "543704476214"
}