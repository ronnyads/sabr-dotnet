variable "project_name" {
  type = string
}

variable "root_domain" {
  type = string
}

variable "cloudfront_certificate_arn" {
  type = string
}

variable "tags" {
  type    = map(string)
  default = {}
}
