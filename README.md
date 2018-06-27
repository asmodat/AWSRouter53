# AWSRouter53
Allows to easily assign AWS Route53 records based on EC2 tags

To create record assign following tags to any EC2 instance:

>Route53 Enable : true / false
>
>Route53 Name : any valid uri name
>
>Route53 Zone : Zone ID
>
>Route53 Address : public / private [optional]


This lambda supports up to 10 diffrent routes, to create multiple ones simply, simply replace * with numbers 1-9

>Route53 Enable *
>
>Route53 Name *
>
>Route53 Zone *
>
>Route53 Address *