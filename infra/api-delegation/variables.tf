variable "aws_region" {
  type        = string
  description = "AWS region. Route 53 is global; this matches the other stacks."
}

variable "zone_name" {
  type        = string
  description = "Parent zone in this account."
}

variable "record_name" {
  type        = string
  description = "Name delegated to the production zone."
}

variable "name_servers" {
  type        = list(string)
  description = "Name servers of the production zone. The API workflow passes these from that zone's output."
}
