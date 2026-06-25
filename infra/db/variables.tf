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

variable "aws_account_id" {
  type    = string
  default = "543704476214"
}